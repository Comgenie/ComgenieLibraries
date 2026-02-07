using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Comgenie.Server.Utils
{
    /// <summary>
    /// This is to wrap around an unseekable stream and allow rewinding the stream a little bit.
    /// It can be used in cases where retrieving large chunks of data is faster, but the data first needs to read an undefined amount of data before knowing how much data to read.
    /// </summary>
    public class RewindableStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly int _historyBufferSize; // Max size of the ring buffer
        private readonly int _maxReadaheadBytes; // Max bytes to read from inner stream at once

        private readonly byte[] RingBuffer;
        private int RingBufferWritePos;
        private int RingBufferDataLength; // Number of bytes currently stored in the ring buffer

        private long TotalBytesReadFromInnerStream; // Total bytes ever read from _innerStream
        private long CurrentLogicalPosition;  // Current logical read position in the stream (accounts for rewinds)

        private readonly byte[] ReadaheadTempBuffer;
        private bool StreamEnded;

        /// <summary>
        /// Initializes a new instance of the <see cref="RewindableStream"/> class.
        /// </summary>
        /// <param name="innerStream">The stream to read from.</param>
        /// <param name="bufferSize">The size of the ring buffer to keep for rewinding. Must be greater than 0.</param>
        /// <param name="maxReadaheadBytes">The maximum number of bytes to read from the inner stream in a single operation
        /// when new data is needed. Must be greater than 0.</param>
        /// <exception cref="ArgumentNullException">Thrown if innerStream is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if bufferSize or maxReadaheadBytes is not positive.</exception>
        public RewindableStream(Stream innerStream, int bufferSize=64*1024, int maxReadaheadBytes = 32*1024)
        {
            if (innerStream == null)
                throw new ArgumentNullException(nameof(innerStream));
            if (!innerStream.CanRead)
                throw new ArgumentException("Inner stream must be readable.", nameof(innerStream));
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be greater than 0.");
            if (maxReadaheadBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxReadaheadBytes), "Max readahead bytes must be greater than 0.");

            _innerStream = innerStream;
            _historyBufferSize = bufferSize;
            _maxReadaheadBytes = maxReadaheadBytes;

            RingBuffer = new byte[_historyBufferSize];
            ReadaheadTempBuffer = new byte[_maxReadaheadBytes];

            ResetBuffer();

            StreamEnded = false;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => _innerStream.CanWrite;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get
            {
                return _innerStream.Position;
            }
            set
            {
                ResetBuffer();
                _innerStream.Position = value;
            }
        }
        private void ResetBuffer()
        {
            RingBufferWritePos = 0;
            RingBufferDataLength = 0;
            TotalBytesReadFromInnerStream = 0;
            CurrentLogicalPosition = 0;
        }
        /// <summary>
        /// Rewinds the stream by a specified number of bytes, if available in the buffer.
        /// </summary>
        /// <param name="numberOfBytes">The number of bytes to rewind.</param>
        /// <returns>The actual number of bytes rewound, which may be less than requested if the buffer limit is reached.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the stream is disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if numberOfBytes is negative.</exception>
        public int Rewind(int numberOfBytes)
        {
            if (numberOfBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(numberOfBytes), "Number of bytes to rewind cannot be negative.");
            if (numberOfBytes == 0)
                return 0;

            long oldestAvailableLogicalPos = TotalBytesReadFromInnerStream - RingBufferDataLength;
            long targetPos = CurrentLogicalPosition - numberOfBytes;

            int actualRewoundBytes;
            if (targetPos < oldestAvailableLogicalPos)
            {
                actualRewoundBytes = (int)(CurrentLogicalPosition - oldestAvailableLogicalPos);
                CurrentLogicalPosition = oldestAvailableLogicalPos;
            }
            else
            {
                CurrentLogicalPosition = targetPos;
                actualRewoundBytes = numberOfBytes;
            }
            return actualRewoundBytes;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }
        
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArgs(buffer, offset, count);

            if (count == 0)
                return 0;

            int totalBytesCopiedToUser = 0;
            bool firstRead = true; // We'll only do one read from the inner stream, because we don't want this call to block just because there were less bytes than the requested count were available
            while (count > 0)
            {
                // Calculate how much data is available in the history buffer for the current logical position
                long oldestAvailableLogicalPosInHistory = TotalBytesReadFromInnerStream - RingBufferDataLength;

                // Ensure we are not trying to read before the buffered data
                if (CurrentLogicalPosition < oldestAvailableLogicalPosInHistory)
                {
                    // This should ideally not happen if Rewind is used correctly.
                    // It means the requested position is older than what's buffered.
                    break;
                }

                int bytesAvailableInHistoryAtCurrentPosition = 0;
                if (CurrentLogicalPosition < TotalBytesReadFromInnerStream) // Is the current read position within data already fetched?
                {
                    bytesAvailableInHistoryAtCurrentPosition = (int)(TotalBytesReadFromInnerStream - CurrentLogicalPosition);
                }

                if (bytesAvailableInHistoryAtCurrentPosition > 0)
                {
                    int bytesToCopyFromHistory = Math.Min(count, bytesAvailableInHistoryAtCurrentPosition);

                    long offsetInHistoryData = CurrentLogicalPosition - oldestAvailableLogicalPosInHistory;
                    int historyReadStartIndex = (RingBufferWritePos - RingBufferDataLength + (int)offsetInHistoryData + _historyBufferSize) % _historyBufferSize;

                    for (int i = 0; i < bytesToCopyFromHistory; ++i)
                    {
                        buffer[offset + totalBytesCopiedToUser + i] = RingBuffer[(historyReadStartIndex + i) % _historyBufferSize];
                    }

                    CurrentLogicalPosition += bytesToCopyFromHistory;
                    totalBytesCopiedToUser += bytesToCopyFromHistory;
                    count -= bytesToCopyFromHistory;

                    if (count == 0)
                        break; // Request satisfied
                }
                else // Need to read new data from inner stream or EOF reached
                {
                    if (StreamEnded || !firstRead)
                        break; // Inner stream has no more data

                    int bytesReadFromInner =await _innerStream.ReadAsync(ReadaheadTempBuffer, 0, _maxReadaheadBytes, cancellationToken);
                    firstRead = false;

                    if (bytesReadFromInner == 0)
                    {
                        StreamEnded = true;
                        break; // EOF reached on inner stream
                    }

                    // Add newly read data to the history buffer
                    for (int i = 0; i < bytesReadFromInner; ++i)
                    {
                        RingBuffer[RingBufferWritePos] = ReadaheadTempBuffer[i];
                        RingBufferWritePos = (RingBufferWritePos + 1) % _historyBufferSize;
                        if (RingBufferDataLength < _historyBufferSize)
                        {
                            RingBufferDataLength++;
                        }
                    }
                    TotalBytesReadFromInnerStream += bytesReadFromInner;

                    // Loop again to attempt to satisfy the remaining 'count' from the newly buffered data.
                    // If the first part of the loop (reading from history) can now satisfy the request, it will.
                }
            }
            return totalBytesCopiedToUser;
        }

        /// <summary>
        /// Read data from inner stream and stop early when bytesToFind is found.
        /// Note: This will call the Async version, it is prefered to use the async method directly.
        /// </summary>
        /// <param name="buffer">Buffer to fill when reading data from the inner stream</param>
        /// <param name="bytesToFind">Byte combination to find and stop reading (rewinds the stream till end of those characters)</param>
        /// <param name="startingPos"></param>
        /// <param name="rewindWhenNotFound">If the byte combination is not found, set to true to rewind as if no read action was done.</param>
        /// <returns>Numberf of bytes read</returns>
        public int ReadTillBytes(byte[] buffer, byte[] bytesToFind, int startingPos = 0, bool rewindWhenNotFound = false)
        {
            return ReadTillBytesAsync(buffer, bytesToFind, startingPos, rewindWhenNotFound).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Read data from inner stream and stop early when bytesToFind is found.
        /// </summary>
        /// <param name="buffer">Buffer to fill when reading data from the inner stream</param>
        /// <param name="bytesToFind">Byte combination to find and stop reading (rewinds the stream till end of those characters)</param>
        /// <param name="startingPos"></param>
        /// <param name="rewindWhenNotFound">If the byte combination is not found, set to true to rewind as if no read action was done.</param>
        /// <param name="cancellationToken">Optional: Cancellation token to cancel this read action</param>
        /// <returns>Numberf of bytes read</returns>
        public async Task<int> ReadTillBytesAsync(byte[] buffer, byte[] bytesToFind, int startingPos = 0, bool rewindWhenNotFound = false, CancellationToken cancellationToken = default)
        {
            int bytesAvailableInHistoryAtCurrentPosition = 0;
            if (CurrentLogicalPosition < TotalBytesReadFromInnerStream) // Is the current read position within data already fetched?
            {
                bytesAvailableInHistoryAtCurrentPosition = (int)(TotalBytesReadFromInnerStream - CurrentLogicalPosition);
            }

            var bufferPos = 0;
            var posCharactersEnd = -1;
            while (bufferPos < buffer.Length && posCharactersEnd < 0)
            {
                int len;
                if (bytesAvailableInHistoryAtCurrentPosition > 0 && bytesAvailableInHistoryAtCurrentPosition < buffer.Length - bufferPos)
                {
                    // First we'll force to read the data from the history buffer
                    // This is needed because network streams will block if we try to read more than the available data, even if the thing we need is in the history buffer
                    len = await ReadAsync(buffer, bufferPos, bytesAvailableInHistoryAtCurrentPosition, cancellationToken);
                }
                else
                {
                    len = await ReadAsync(buffer, bufferPos, buffer.Length - bufferPos, cancellationToken);
                }

                if (len <= 0)
                {
                    if (rewindWhenNotFound)
                        this.Rewind(bufferPos);
                    return 0;
                }

                for (var i = bufferPos; i < bufferPos + len; i++)
                {
                    if (i < bytesToFind.Length || i - bytesToFind.Length < startingPos)
                        continue;

                    var allCorrect = true;
                    for (var j = 0; j < bytesToFind.Length; j++)
                    {
                        if (buffer[i - j] != bytesToFind[(bytesToFind.Length - 1) - j])
                        {
                            allCorrect = false;
                            break;
                        }
                    }

                    if (allCorrect)
                    {
                        posCharactersEnd = i + 1;
                        break;
                    }
                }
                bufferPos += len;
            }

            if (posCharactersEnd < 0)
            {
                if (rewindWhenNotFound)
                    this.Rewind(bufferPos);
                return 0;
            }

            this.Rewind(bufferPos - posCharactersEnd);
            return posCharactersEnd;
        }

        /// <summary>
        /// Flush the inner stream
        /// </summary>
        public override void Flush()
        {
            _innerStream.Flush();
        }

        /// <summary>
        /// Seek the inner stream, this also clears the rewind buffer.
        /// </summary>
        /// <param name="offset">Offset from the origin to set the new position</param>
        /// <param name="origin">Origin to set the new position</param>
        /// <returns>The new position within the inner stream</returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            ResetBuffer();
            return _innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _innerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _innerStream.Write(buffer, offset, count);
        }


        private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");
            if (buffer.Length - offset < count)
                throw new ArgumentException("Invalid offset and/or count for the given buffer.");
        }
    }
}
