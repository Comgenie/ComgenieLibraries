using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    public class SubStream : Stream
    {
        public Stream InnerStream = null;
        public long CurLength = 0;
        public long CurOffset = 0;
        public long CurPosition = 0;
        public SubStream(Stream originalStream, long offset, long length)
        {
            InnerStream = originalStream;
            CurLength = length;
            CurOffset = offset;
            originalStream.Position = offset;
            CurPosition = 0;
            
        }

        public override bool CanRead => InnerStream.CanRead;

        public override bool CanSeek => InnerStream.CanSeek;

        public override bool CanWrite => InnerStream.CanWrite;

        public override long Length => CurLength;

        public override long Position {
            get => CurPosition;
            set => CurPosition = (value - CurOffset);
        }

        public override void Flush()
        {
            InnerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (CurPosition < 0)
            {
                CurPosition = 0;
                InnerStream.Position = CurOffset;
            }

            int bytesRead = 0;
            if (CurPosition >= CurLength)
                return 0;

            InnerStream.Position = CurPosition + CurOffset;
            if (CurPosition + count >= CurLength)
            {
                // Only return partial 
                bytesRead = InnerStream.Read(buffer, offset, (int)(CurLength - CurPosition));
            }
            else
            {
                // Return full
                bytesRead = InnerStream.Read(buffer, offset, count);
            }

            CurPosition += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            CurPosition = InnerStream.Seek(offset + CurOffset, origin) - CurOffset;
            return CurPosition;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // TODO: Make sure this write action doesn't pass the substream boundary
            InnerStream.Write(buffer, offset, count);
            CurPosition += count;
        }
    }
}
