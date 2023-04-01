using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    public class ChunkedStream : Stream
    {
        public Stream InnerStream = null;
        public byte[] CurrentBuffer = new byte[1024*32];
        public int BufferLength = 0;
        public int BufferPos = 0;
        private bool HadLastRead = false;
        private bool HadFirstResponse = false;
        private bool EnableGZipCompression = false;

        private GZipStream GZipStream = null;
        private MemoryStream CompressedData = null;
        public ChunkedStream(Stream originalStream, bool enableGZipCompression=false)
        {
            InnerStream = originalStream;
            EnableGZipCompression = enableGZipCompression;

            if (enableGZipCompression)
            {
                CompressedData = new MemoryStream();
                GZipStream = new GZipStream(CompressedData, CompressionLevel.Fastest, true);
            }
        }
        public override void Close()
        {
            base.Close();
            if (GZipStream != null)
                GZipStream.Dispose();
            if (CompressedData != null)
                CompressedData.Dispose();
            if (InnerStream != null)
                InnerStream.Dispose();

            GZipStream= null;
            CompressedData = null;
            InnerStream = null;
        }

        public override bool CanRead => InnerStream.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => InnerStream.Length;

        public override long Position {
            get => InnerStream.Position;
            set => InnerStream.Position = value;
        }

        public override void Flush()
        {
            InnerStream.Flush();
        }
        public string FullResponse { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (BufferPos == BufferLength)
            {
                if (HadLastRead)
                    return 0;

                BufferLength = InnerStream.Read(CurrentBuffer, 0, CurrentBuffer.Length - 10);
                if (BufferLength == 0)
                    HadLastRead = true;

                if (EnableGZipCompression && BufferLength > 0) // I wish the c# framework GZipStream was written so I could just put this stream around that stream.. but nope
                {                    
                    GZipStream.Write(CurrentBuffer, 0, BufferLength);
                    GZipStream.Flush();

                    if (HadLastRead)
                    {
                        GZipStream.Dispose();
                        GZipStream = null;
                    }
                        
                    Buffer.BlockCopy(CompressedData.ToArray(), 0, CurrentBuffer, 0, (int)CompressedData.Length);
                    BufferLength = (int)CompressedData.Length;

                    CompressedData.SetLength(0);
                }

                // Add content-length part in front of the buffer, note: for some reason chunked transfer requires hex-content-length values
                var bytesPrefix = ASCIIEncoding.ASCII.GetBytes((HadFirstResponse ? "\r\n" : "") + BufferLength.ToString("X") + "\r\n" + (HadLastRead ? "\r\n" : ""));
                HadFirstResponse = true;

                Buffer.BlockCopy(CurrentBuffer, 0, CurrentBuffer, bytesPrefix.Length, BufferLength);                
                Buffer.BlockCopy(bytesPrefix, 0, CurrentBuffer, 0, bytesPrefix.Length);
                BufferLength += bytesPrefix.Length;

                FullResponse += ASCIIEncoding.ASCII.GetString(CurrentBuffer, 0, BufferLength);

                BufferPos = 0;
            }

            if (count > BufferLength - BufferPos)
                count = BufferLength - BufferPos;

            Buffer.BlockCopy(CurrentBuffer, BufferPos, buffer, offset, count);
            BufferPos += count;

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException(); // Read only stream
        }
    }
}
