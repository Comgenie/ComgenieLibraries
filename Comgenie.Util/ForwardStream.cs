using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Util
{
    /// <summary>
    /// This ForwardStream class acts as a buffer when both the provider of the data, as the consumer of the data only accepts a stream and never provides a stream. 
    /// Within Comgenie.Storage this is used to be able to return a write-able stream to the rest of the code, while it's also already passed to an .UploadFromStream() call
    /// </summary>
    public class ForwardStream : Stream, IDisposable
    {
        // If set to true, the writer will be captured when disposing the stream, until the reader explicitly calls .ReleaseWriter();
        public bool CaptureWriter { get; set; } = false;

        private byte[] Buffer = new byte[1024 * 10];

        // If BufferStart & BufferEnd are the same, then the buffer is empty
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
            // This method only writes to 'BufferStart'            
            if (StreamEnded && BufferEnd == BufferStart)
                return 0;

            // Read from ring buffer
            while (BufferEnd == BufferStart && !StreamEnded)
                Thread.Sleep(10); // Wait till buffer is filled

            var curEnd = BufferEnd; // Copy so the code is threadsafe
            var curLen = 0;

            if (BufferStart > curEnd)
                curLen = Buffer.Length - BufferStart; // Rest of the remaining buffer before looping back
            else
                curLen = curEnd - BufferStart;

            if (count < curLen)
                curLen = count;

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
            // This method only writes to BufferEnd

            // Add to ring buffer
            while (count > 0)
            {                
                var curStart = BufferStart; // Copy so the code is threadsafe
                curStart = curStart == 0 ? (Buffer.Length - 1) : (curStart - 1); // Never use the full length of the ring buffer, so we can see the difference between full or empty

                var curCount = 0;

                if (BufferEnd <= curStart)
                    curCount = curStart - BufferEnd;
                else
                    curCount = Buffer.Length - BufferEnd;

                if (curCount == 0)
                {
                    Thread.Sleep(10); // Wait till buffer is emptied
                    continue;
                }

                if (count < curCount)
                    curCount = count;

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
