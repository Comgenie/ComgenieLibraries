using Comgenie.Util.ReedSolomonNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Comgenie.Util
{
    /// <summary>
    /// This this a stream which saves the data to the inner stream encrypted and optionally repairable
    /// It does it by storing data in same size chunks, with a small header for each chunk containing the IV
    /// Any write to that block will cause the block to be rewritten with a different IV (when flusing or disposing the stream)
    /// </summary>
    public class EncryptedAndRepairableStream : Stream
    {
        /// <summary>
        /// Optional callback when disposing the stream, with a bool indicating if the stream was written to or not. 
        /// This callback is useful in case the stream is passed away to another bit of code, but you still want to do any post actions on it.
        /// </summary>
        public Action<bool>? OnDispose { get; set; } // Custom callback when disposing, with a bool indicating if the stream was written to or not
        private bool StreamWasWrittenTo { get; set; } = false;

        private Aes AesEncryption;

        // Sizes of each field (some depends on the repair data setting in the constructor)
        private int DataBlockSize = 512;
        private int IVSize = 16;
        private int LenFieldSize = 2;
        private int ChecksumSize = 4;
        private int RepairSize = 0;
        private int RepairChecksumSize = 0;
        private int FullBlockSize = 0;
        private int HeaderSize = 0;

        // We've hardcoded this one to be about 25% of repair data
        private int RepairShardDataCount = 6;
        private int RepairShardRepairCount = 2;
        

        private byte[] RawBlockBuffer { get; set; }
        private byte[] FullBlockBuffer { get; set; }

        private int CurrentBlockLength { get; set; }
        private bool CurrentBlockBufferWritten { get; set; }
        private int CurrentBlockIndex { get; set; } = -1;

        private Stream? InnerStream { get; set; } // stream of encrypted and repairable file
        private long InnerPosition { get; set; } // position in the actual encrypted and repairable file
        private long InnerLength { get; set; }

        private long OuterPosition { get; set; } // position in the unencrypted data
        private long OuterLength { get; set; }

        /// <summary>
        /// Create a new EncryptedAndRepairableStream. Encryption is mandatory, repair data is optional.
        /// Note that re-opening any stream will require the same parameters.
        /// </summary>
        /// <param name="innerStream">Stream to encrypt and optionally add repair data</param>
        /// <param name="encryptionKey">A strong encryption key, any length is accepted but longer is better</param>
        /// <param name="includeRepairData">Optionally, option to include repair data. When reading a corrupted stream, the data is will be automatically repaired in the read buffer.</param>
        public EncryptedAndRepairableStream(Stream innerStream, byte[] encryptionKey, bool includeRepairData=false)
        {
            InnerStream = innerStream;
            AesEncryption = Aes.Create();
            AesEncryption.Padding = PaddingMode.None; // We put it in our own blocks
            var hash = SHA256.Create().ComputeHash(encryptionKey);
            AesEncryption.Key = hash;

            if (includeRepairData) // We will divide each data block into multiple shards, and generate additional parity shards
            {
                RepairSize = DataBlockSize + IVSize + LenFieldSize;
                if (RepairSize % RepairShardDataCount != 0)
                    RepairSize = RepairSize + (RepairShardDataCount - RepairSize % RepairShardDataCount);
                 
                RepairChecksumSize = (ChecksumSize * (RepairShardDataCount + RepairShardRepairCount));
                RepairSize = ((DataBlockSize / RepairShardDataCount) * RepairShardRepairCount) + RepairChecksumSize * 2;
            }

            HeaderSize = LenFieldSize + IVSize + ChecksumSize + RepairSize;
            FullBlockSize = HeaderSize + DataBlockSize;

            InnerPosition = innerStream.Position;
            InnerLength = innerStream.Length;

            // Get the full length
            // TODO: Only do this in case the length is actually needed
            OuterLength = (innerStream.Length / (long)FullBlockSize) * (long)DataBlockSize;

            if (innerStream.Length % FullBlockSize > HeaderSize)
            {
                // The last block may be less than the full size.
                // However we have to look at the length field in the header to correctly know how many data bytes are actually in there
                if (innerStream.CanSeek)
                {
                    var origPos = innerStream.Position;
                    var totalBlocks = (innerStream.Length / (long)FullBlockSize); // This rounds it down to number of full blocks
                    totalBlocks *= (long)FullBlockSize; // Back to the position of the inner stream of the final block

                    totalBlocks += ChecksumSize + RepairSize;
                    innerStream.Seek(totalBlocks, SeekOrigin.Begin);
                    byte[] number = new byte[2];
                    innerStream.ReadExactly(number);

                    var lastBlockActualLength = BitConverter.ToUInt16(number);

                    OuterLength += lastBlockActualLength;

                    innerStream.Seek(origPos, SeekOrigin.Begin);
                }
                else
                {
                    Debug.WriteLine("Warning: Estimating total length because the inner stream is not seekable", nameof(EncryptedAndRepairableStream));
                    OuterLength += innerStream.Length % FullBlockSize - HeaderSize;
                }
            }

            // Estimate the outer position based on the location within the inner position
            if (innerStream.Length == innerStream.Position)
            {
                // Set to the end of the inner stream, we already have that one
                OuterPosition = OuterLength;
            }
            else
            {
                long startBlock = (innerStream.Position / (long)FullBlockSize); // Rounds down based on full (written) block sizes
                OuterPosition = startBlock * (long)DataBlockSize; // Convert to actual stored data block sizes.

                if (innerStream.Position % FullBlockSize > HeaderSize)
                {
                    // Estimate
                    Debug.WriteLine("Warning: A non-zero inner stream position may have unexpected results, unless set the end of the inner stream or in a multiple of " + FullBlockSize, nameof(EncryptedAndRepairableStream));
                    OuterPosition += (innerStream.Position % FullBlockSize) - HeaderSize;
                    if (OuterPosition > OuterLength)
                        OuterPosition = OuterLength;
                }
                else if (innerStream.Position % FullBlockSize > 0)
                {
                    if (innerStream.CanSeek)
                    {
                        // Seek to the start of the full block
                        innerStream.Seek(startBlock, SeekOrigin.Begin);
                    }
                    else
                    {
                        Debug.WriteLine("Warning: The inner stream position is not set to an exact boundary of a block and is not seekable.", nameof(EncryptedAndRepairableStream));
                    }
                }
            }

            RawBlockBuffer = new byte[DataBlockSize];
            FullBlockBuffer = new byte[FullBlockSize];
        }

        /// <summary>
        /// True if .Read() can be used for this stream
        /// </summary>
        public override bool CanRead => InnerStream?.CanRead ?? false;

        /// <summary>
        /// True if .Seek() can be used for this stream
        /// </summary>
        public override bool CanSeek => InnerStream?.CanSeek ?? false;

        /// <summary>
        /// True if .Write() can be used for this stream
        /// </summary>
        public override bool CanWrite => InnerStream?.CanWrite ?? false;

        /// <summary>
        /// Length of this stream.
        /// Note that this is the length of the unencrypted side of the data, not the actual file size length.
        /// </summary>
        public override long Length => OuterLength;

        /// <summary>
        /// Get or sets the position within this stream.
        /// Note that this is the position within the unencrypted side of the data, not the actual position as written to a file.
        /// </summary>
        public override long Position
        {
            get { return OuterPosition; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        /// <summary>
        /// Flush all pending (buffered) changes. This also calls .Flush() on the inner stream.
        /// </summary>
        public override void Flush()
        {
            if (InnerStream == null)
                return;

            WriteBlockFromBuffer();
            InnerStream.Flush();
        }

        /*
            Block format:  [Checksum] [ x Repair Data ] + [ 2 byte len ] + [ 16 IV ] + [ 512 data ] 
             Checksum is calculated over everything after that
             Repair data is calculated over everything after that
             Repair data format:  [Checksum * (DataShardCount+RepairDataCount] + [ Repair Shard ] + [ Repair Shard ] + .. + [ Another checksum block ]
         */
        private bool ReadBlockToBuffer()
        {
            if (InnerStream == null)
                return false;

            bool repaired = false;
            var startBlockPos = (long)FullBlockSize * (long)CurrentBlockIndex;
            if (InnerLength < startBlockPos)
            {
                CurrentBlockLength = 0;
                return false;
            }

            if (InnerPosition != startBlockPos)
            {
                if (!InnerStream.CanSeek)
                {
                    // Forward if possible
                    if (startBlockPos < InnerPosition)
                        throw new Exception("Cannot read to this position because the inner stream does not accept seeking");
                    while (InnerPosition < startBlockPos)
                    {
                        var readAmount = startBlockPos - InnerPosition;
                        if (readAmount > FullBlockBuffer.Length)
                            readAmount = FullBlockBuffer.Length;
                        var tmpRead = InnerStream.Read(FullBlockBuffer, 0, (int)readAmount);
                        if (tmpRead == 0)
                            throw new Exception("Cannot forward behind file");
                        InnerPosition += tmpRead;
                    }
                }
                else
                {
                    InnerStream.Seek(startBlockPos, SeekOrigin.Begin);
                }
                InnerPosition = startBlockPos;
            }


            var left = FullBlockSize;
            while (left > 0)
            {
                var curRead = InnerStream.Read(FullBlockBuffer, FullBlockSize - left, left);
                if (curRead == 0) // end of file
                {
                    break;
                }
                left -= curRead;
                InnerPosition += curRead;
            }

            var innerLen = FullBlockSize - left;
            if (innerLen < HeaderSize)
            {
                CurrentBlockLength = 0;
                return false;
            }
            CurrentBlockLength = innerLen - HeaderSize;

            if (CurrentBlockLength % 16 > 0)
                CurrentBlockLength = CurrentBlockLength + (16 - CurrentBlockLength % 16);

            // Check checksum
            var storedChecksum = BitConverter.ToUInt32(FullBlockBuffer, 0);
            var currentChecksum = CRC32.CalculateCRC32(FullBlockBuffer, ChecksumSize, innerLen - ChecksumSize);
            if (storedChecksum != currentChecksum)
            {
                // Repair if possible
                if (RepairSize == 0)
                    throw new Exception("Checksum doesn't match and no repair data available.");
                
                var corruptDataLength = innerLen - (ChecksumSize + RepairSize);
                var startRepairableData = ChecksumSize + RepairSize;
                var shardSize = (RepairSize - RepairChecksumSize * 2) / RepairShardRepairCount;
                var shards = new byte[RepairShardDataCount + RepairShardRepairCount][];
                var remainingDataLen = corruptDataLength;
                for (int i = 0; i < shards.Length; i++)
                {
                    shards[i] = new byte[shardSize];
                    if (i >= RepairShardDataCount) // Repair data
                    {
                        Buffer.BlockCopy(FullBlockBuffer, ChecksumSize + RepairChecksumSize + ((i - RepairShardDataCount) * shardSize), shards[i], 0, shardSize);
                    }
                    else
                    {
                        // Normal data
                        if (remainingDataLen == 0)
                            continue;
                        var lenShardData = shardSize;
                        if (remainingDataLen < lenShardData)
                            lenShardData = remainingDataLen;
                        Buffer.BlockCopy(FullBlockBuffer, startRepairableData + (i * shardSize), shards[i], 0, lenShardData);
                        remainingDataLen -= lenShardData;
                    }
                }

                // Check checksums
                var present = new bool[shards.Length];
                for (int i = 0; i < shards.Length; i++)
                {
                    var storedShardChecksum = BitConverter.ToUInt32(FullBlockBuffer, ChecksumSize + (i * ChecksumSize));
                    var currentShardChecksum = CRC32.CalculateCRC32(shards[i], 0, shardSize);
                    present[i] = (storedShardChecksum == currentShardChecksum);
                }

                if (present.Where(a => !a ).Count() > RepairShardRepairCount)
                {
                    // There are multiple checksums invalid, todo, use backup checksum at end of repair data
                    for (int i = 0; i < shards.Length; i++)
                    {
                        var storedShardChecksum = BitConverter.ToUInt32(FullBlockBuffer, ChecksumSize + (RepairSize - RepairChecksumSize) + (i * ChecksumSize));
                        var currentShardChecksum = CRC32.CalculateCRC32(shards[i], 0, shardSize);
                        present[i] = (storedShardChecksum == currentShardChecksum);
                    }

                    if (present.Where(a => !a).Count() > RepairShardRepairCount)
                    {
                        // TODO: Use the full block checksum + brute force repair as a last fallback
                        throw new Exception("Could not repair data in file");
                    }

                }

                var reedSolomon = ReedSolomonNet.ReedSolomon.Create(RepairShardDataCount, RepairShardRepairCount);
                reedSolomon.DecodeMissing(shards, present, 0, shardSize);

                if (!reedSolomon.IsParityCorrect(shards, 0, shardSize))
                    throw new Exception("Could not repair data in file");

                // Copy repaired data back
                remainingDataLen = corruptDataLength;
                for (int i = 0; i < shards.Length - RepairShardRepairCount; i++)
                {
                    if (remainingDataLen == 0)
                        break;

                    var lenShardData = shardSize;
                    if (remainingDataLen < lenShardData)
                        lenShardData = remainingDataLen;
                    Buffer.BlockCopy(shards[i], 0, FullBlockBuffer, startRepairableData + (i * shardSize), lenShardData);
                    remainingDataLen -= lenShardData;
                }

                // Also regenerate checksum as it can also be corrupted
                var checksum = CRC32.CalculateCRC32(FullBlockBuffer, ChecksumSize, CurrentBlockLength + HeaderSize - ChecksumSize);
                BitConverter.GetBytes(checksum).CopyTo(FullBlockBuffer, 0);

                repaired = true;
            }

            // Decrypt data
            byte[] iv = new byte[16];
            Buffer.BlockCopy(FullBlockBuffer, ChecksumSize + RepairSize + LenFieldSize, iv, 0, 16);
            using (var decryptor = AesEncryption.CreateDecryptor(AesEncryption.Key, iv))
                decryptor.TransformBlock(FullBlockBuffer, HeaderSize, CurrentBlockLength, RawBlockBuffer, 0);

            // Set length to actual data contents, the encrypted data can be larger
            CurrentBlockLength = BitConverter.ToUInt16(FullBlockBuffer, ChecksumSize + RepairSize);

            if (CurrentBlockLength < DataBlockSize) // last block, update the length to the exact number
                OuterLength = ((long)CurrentBlockIndex * (long)DataBlockSize) + (long)CurrentBlockLength;

            return repaired;
        }

        private void WriteBlockFromBuffer()
        {
            if (!CurrentBlockBufferWritten || InnerStream == null)
                return;

            var startBlockPos = (long)FullBlockSize * (long)CurrentBlockIndex;
            if (InnerPosition != startBlockPos)
            {
                if (!InnerStream.CanSeek)
                    throw new Exception("Cannot write to this position because the inner stream does not accept seeking");
                InnerStream.Seek(startBlockPos, SeekOrigin.Begin);
                InnerPosition = startBlockPos;
            }

            var origLen = (ushort)CurrentBlockLength;
            BitConverter.GetBytes(origLen).CopyTo(FullBlockBuffer, ChecksumSize + RepairSize);

            // Expand because encrypted data needs to be stored in blocks of 16 bytes
            if (CurrentBlockLength % 16 > 0)
                CurrentBlockLength = CurrentBlockLength + (16 - CurrentBlockLength % 16);

            // Encrypt
            AesEncryption.GenerateIV();
            Buffer.BlockCopy(AesEncryption.IV, 0, FullBlockBuffer, ChecksumSize + RepairSize + LenFieldSize, AesEncryption.IV.Length);
            using (var encryptor = AesEncryption.CreateEncryptor())
                encryptor.TransformBlock(RawBlockBuffer, 0, CurrentBlockLength, FullBlockBuffer, HeaderSize);

            // Generate repair data for both IV and Data
            if (RepairSize != 0)
            {
                // Split the current block buffer into 4 shards, generate 1 parity shard, save that as repair data                
                var actualLenRepairableData = CurrentBlockLength + IVSize + LenFieldSize;
                var startRepairableData = ChecksumSize + RepairSize;
                var reedSolomon = ReedSolomonNet.ReedSolomon.Create(RepairShardDataCount, RepairShardRepairCount);
                var shards = new byte[RepairShardDataCount + RepairShardRepairCount][];
                var shardSize = (RepairSize - RepairChecksumSize * 2) / RepairShardRepairCount;
                for (var i=0;i<shards.Length;i++)
                {
                    shards[i] = new byte[shardSize];

                    if (i < RepairShardDataCount && actualLenRepairableData > 0)
                    {
                        // Note that the last block can be smaller than the full block size, we will just act like all remaining data are 0's then
                        var lenShardData = shardSize;
                        if (actualLenRepairableData < lenShardData)
                            lenShardData = actualLenRepairableData;
                        Buffer.BlockCopy(FullBlockBuffer, startRepairableData + (i * shardSize), shards[i], 0, lenShardData);
                        actualLenRepairableData -= lenShardData;
                    }
                }
                reedSolomon.EncodeParity(shards, 0, shardSize);

                if (!reedSolomon.IsParityCorrect(shards, 0, shardSize))
                    throw new Exception("Check failed");

                // Set checksums at beginning and end of repair data for each shard
                byte[] shardChecksumData = new byte[RepairChecksumSize];
                for (var i=0;i<shards.Length;i++)
                {
                    var checksumShard = CRC32.CalculateCRC32(shards[i], 0, shardSize);
                    BitConverter.GetBytes(checksumShard).CopyTo(shardChecksumData, i * ChecksumSize);
                }
                Buffer.BlockCopy(shardChecksumData, 0, FullBlockBuffer, ChecksumSize, RepairChecksumSize);
                for (var i=0;i<RepairShardRepairCount;i++)
                {
                    Buffer.BlockCopy(shards[RepairShardDataCount + i], 0, FullBlockBuffer, ChecksumSize + RepairChecksumSize + (shardSize * i), shardSize);
                }
                Buffer.BlockCopy(shardChecksumData, 0, FullBlockBuffer, ChecksumSize + (RepairSize - RepairChecksumSize), RepairChecksumSize);
            }

            // Generate checksum
            var checksum = CRC32.CalculateCRC32(FullBlockBuffer, ChecksumSize, CurrentBlockLength + HeaderSize - ChecksumSize);
            BitConverter.GetBytes(checksum).CopyTo(FullBlockBuffer, 0);

            // TODO: Optionally replace checksum by sha256 hash data+key+blockindex

            InnerStream.Write(FullBlockBuffer, 0, CurrentBlockLength + HeaderSize);
            InnerPosition += CurrentBlockLength + HeaderSize;

            CurrentBlockBufferWritten = false;
            StreamWasWrittenTo = true;
        }

        /// <summary>
        /// Check and repair the full file. This will read the full file, detect issues and write it back repaired.
        /// </summary>
        /// <returns>The number of blocks repaired in this file</returns>
        public int Repair()
        {
            // Check and repair full file
            if (InnerStream == null)
                return 0;

            CurrentBlockIndex = 0;
            var repairedCount = 0;
            while (true)
            {
                var repaired = ReadBlockToBuffer();
                if (repaired)
                {
                    CurrentBlockBufferWritten = true;
                    WriteBlockFromBuffer();
                    repairedCount++;
                }
                if (CurrentBlockLength < DataBlockSize)
                    return repairedCount;
                CurrentBlockIndex++;
            }
        }

        /// <summary>
        /// Read a number of bytes to the given buffer at the given offset and for a max of the given count.
        /// </summary>
        /// <param name="buffer">A byte buffer which will be populated with data</param>
        /// <param name="offset">The offset where the data population begins</param>
        /// <param name="count">The max number of bytes which will be written to the buffer</param>
        /// <returns>Number of actual bytes written to the buffer. 0 if no more data is available (end of stream).</returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (InnerStream == null)
                return 0;

            int len = 0;
            while (count > 0 && OuterPosition < OuterLength)
            {
                var readInBlock = (int)(OuterPosition / DataBlockSize);
                var readInBlockPos = (int)(OuterPosition % DataBlockSize);
                if (readInBlock != CurrentBlockIndex)
                {
                    WriteBlockFromBuffer(); // Write any previous cached block

                    CurrentBlockIndex = readInBlock;
                    ReadBlockToBuffer();
                }

                var readLength = count < DataBlockSize - readInBlockPos ? count : DataBlockSize - readInBlockPos;
                if (readLength + OuterPosition > OuterLength)
                    readLength = (int)(OuterLength - OuterPosition);
                Buffer.BlockCopy(RawBlockBuffer, readInBlockPos, buffer, offset, readLength);

                len += readLength;
                count -= readLength;
                offset += readLength;
                OuterPosition += readLength;
            }
            return len;
        }

        /// <summary>
        /// Set the position in this stream. Note that this is the position within the unencrypted side of the data, not the actual position as written to a file.
        /// </summary>
        /// <param name="offset">Position in unencrypted data</param>
        /// <param name="origin">Relative to</param>
        /// <returns>The new position within this stream</returns>
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (InnerStream == null)
                return 0;

            // Calculate inner position
            if (origin == SeekOrigin.End)
                offset = OuterLength - offset;
            else if (origin == SeekOrigin.Current)
                offset = OuterPosition + offset;

            var writeInBlock = (int)(offset / DataBlockSize);
            var writeInBlockPos = (int)(offset % DataBlockSize);

            if (writeInBlock != CurrentBlockIndex)
            {
                WriteBlockFromBuffer(); // Write any previous cached block

                CurrentBlockIndex = writeInBlock;
                ReadBlockToBuffer();
            }

            OuterPosition = offset;

            InnerPosition = (long)writeInBlock * (long)FullBlockSize;
            InnerPosition += HeaderSize + writeInBlockPos;

            InnerStream.Seek(InnerPosition, origin);

            return OuterPosition;
        }

        /// <summary>
        /// Not supported yet
        /// </summary>
        /// <param name="value"></param>
        public override void SetLength(long value)
        {
            throw new NotImplementedException();
            // TODO
            //InnerStream.SetLength(value);
        }

        /// <summary>
        /// Encrypt and write data to the inner stream. Note that an internal buffer is used and changes might not be written yet. 
        /// </summary>
        /// <param name="buffer">Byte array containing the data</param>
        /// <param name="offset">Start position within the byte array to start reading data to be written</param>
        /// <param name="count">Number of bytes starting at the offset to write.</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (InnerStream == null)
                return;
            while (count > 0)
            {
                var writeInBlock = (int)(OuterPosition / (long)DataBlockSize);
                var writeInBlockPos = (int)(OuterPosition % (long)DataBlockSize);

                if (writeInBlock != CurrentBlockIndex)
                {
                    WriteBlockFromBuffer(); // Write any previous cached block

                    CurrentBlockIndex = writeInBlock;
                    if (OuterLength > OuterPosition && (count < DataBlockSize || writeInBlockPos > 0)) // Partial overwriting, load current data
                    {
                        ReadBlockToBuffer();
                    }
                    else
                    {
                        // Overwriting a full block, or writing to a new one
                        CurrentBlockLength = 0;
                    }
                }

                var writeLength = count < DataBlockSize - writeInBlockPos ? count : DataBlockSize - writeInBlockPos;
                Buffer.BlockCopy(buffer, offset, RawBlockBuffer, writeInBlockPos, writeLength);
                CurrentBlockBufferWritten = true;

                if (CurrentBlockLength < writeInBlockPos + writeLength)
                    CurrentBlockLength = writeInBlockPos + writeLength;

                count -= writeLength;
                offset += writeLength;
                OuterPosition += writeLength;
                if (OuterLength < OuterPosition)
                    OuterLength = OuterPosition;
            }
        }

        /// <summary>
        /// Write away all pending changes and disposes this and the inner stream.
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            if (InnerStream == null)
                return;

            Flush();
            InnerStream.Dispose();
            InnerStream = null;
            if (OnDispose != null)
                OnDispose(StreamWasWrittenTo);
        }
    }
}
