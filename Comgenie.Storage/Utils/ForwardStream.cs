using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Utils
{
    public class ForwardStream : Stream, IDisposable
    {
        // If set to true, the writer will be captured when disposing the stream, until the reader explicitly calls .ReleaseWriter();
        public bool CaptureWriter { get; set; } = false;

        private byte[] Buffer = new byte[1024 * 10];
        private int BufferStart = 0;
        private int BufferEnd = 0;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }
        public bool StreamEnded = false;

        public override void Flush()
        {

        }
        public void ReleaseWriter()
        {
            CaptureWriter = false;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (StreamEnded && BufferEnd == BufferStart)
                return 0;

            // Read from ring buffer
            while (BufferEnd == BufferStart && !StreamEnded)
                Thread.Sleep(10); // Wait till buffer is filled


            var curEnd = BufferEnd; // Copy so the code is threadsafe
            if (BufferStart > curEnd)
                curEnd += Buffer.Length; // Loops around

            var curLen = curEnd - BufferStart;

            if (count < curLen)
                curLen = count;

            if (curLen + BufferStart > Buffer.Length)
            {
                // Loop around, only return the part till the end of the buffer
                curLen = Buffer.Length - BufferStart;
            }

            Array.Copy(Buffer, BufferStart, buffer, offset, curLen);
            BufferStart = (BufferStart + curLen) % Buffer.Length;
            return curLen;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0;
        }

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Add to ring buffer
            while (count > 0)
            {
                var curStart = BufferStart; // Copy so the code is threadsafe
                curStart = (curStart - 1) % Buffer.Length; // Never use the full length of the ring buffer, so we can see the difference between full or empty

                var curCount = count;

                // Make sure we aren't passing the BufferStart                 
                if (BufferEnd < curStart && curStart - BufferEnd > curCount)
                    curCount = curStart - BufferEnd;

                // If we have to loop around the ring buffer, split this write into multiple writes
                if (curCount > buffer.Length - BufferEnd)
                    curCount = buffer.Length - BufferEnd;

                if (curCount == 0)
                {
                    Thread.Sleep(10); // Wait till buffer is emptied
                    continue;
                }

                Array.Copy(buffer, offset, Buffer, BufferEnd, curCount);

                count -= curCount;
                offset += curCount;
                BufferEnd = (BufferEnd + curCount) % Buffer.Length;
            }
        }
        protected override void Dispose(bool disposing)
        {
            // Make sure the next reads are returning 0 when the buffer is empty
            StreamEnded = true;
            while (CaptureWriter)
                Thread.Sleep(10); // Wait till buffer is emptied
        }
    }
}
