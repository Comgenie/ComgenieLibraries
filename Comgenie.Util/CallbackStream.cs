using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Utils
{
    /// <summary>
    /// Use this CallbackStream to execute and action for each method executed on this stream object. The actions are always executed before the method itself is executed.
    /// </summary>
    public class CallbackStream : Stream, IDisposable
    {
        public Action? OnDispose { get; set; } = null;
        public Action? OnFlush { get; set; } = null;
        public Action<byte[], int, int>? OnRead { get; set; } = null;
        public Action<byte[], int, int>? OnWrite { get; set; } = null;
        public Action<long, SeekOrigin>? OnSeek { get; set; } = null;

        private Stream InnerStream { get; set; }

        public override bool CanRead => InnerStream.CanRead;

        public override bool CanSeek => InnerStream.CanSeek;

        public override bool CanWrite =>InnerStream.CanWrite;

        public override long Length => InnerStream.Length;

        public override long Position { get => InnerStream.Position; set => InnerStream.Position = value; }

        public CallbackStream(Stream innerStream)
        {
            InnerStream = innerStream;
        }

        public override void Flush()
        {
            if (OnFlush != null)
                OnFlush();
            InnerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (OnRead != null)
                OnRead(buffer, offset, count);
            return InnerStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (OnSeek != null)
                OnSeek(offset, origin);
            return InnerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            InnerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (OnWrite != null)
                OnWrite(buffer, offset, count);
            InnerStream.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (OnDispose != null)
                OnDispose();
        }
    }
}
