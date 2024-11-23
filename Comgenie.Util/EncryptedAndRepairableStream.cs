using Comgenie.Utils.ReedSolomonNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Comgenie.Utils
{
    /// <summary>
    /// This this a stream which saves the data to the inner stream encrypted and optionally repairable
    /// It does it by storing data in same size chunks, with a small header for each chunk containing the IV
    /// Any write to that block will cause the block to be rewritten with a different IV (when flusing or disposing the stream)
    /// </summary>
    public class EncryptedAndRepairableStream : Stream
    {
        private Aes AesEncryption;
        private int RawBlockSize = 512;
        private int IVSize = 16;
        private int LenFieldSize = 2;
        private int ChecksumSize = 4;
        private int RepairSize = 0;
        private int RepairChecksumSize = 0;
        private int FullBlockSize = 0;
        private int HeaderSize = 0;

        private int RepairShardDataCount = 6;
        private int RepairShardRepairCount = 2;
        private Stream? InnerStream { get; set; } // stream of encrypted and repairable file

        private byte[] RawBlockBuffer { get; set; }
        private byte[] FullBlockBuffer { get; set; }

        private int CurrentBlockLength { get; set; }
        private bool CurrentBlockBufferWritten { get; set; }
        private int CurrentBlockIndex { get; set; } = -1;

        private long InnerPosition { get; set; } // position in the actual encrypted and repairable file
        private long InnerLength { get; set; }

        private long RawPosition { get; set; } // position in the original unencrypted file
        private long RawLength { get; set; }
        public Action<bool>? OnDispose { get; set; } // Custom callback when disposing, with a bool indicating if the stream was written to or not
        private bool StreamWasWrittenTo { get; set; } = false;
        public EncryptedAndRepairableStream(Stream innerStream, byte[] encryptionKey, bool includeRepairData=false)
        {
            InnerStream = innerStream;
            AesEncryption = Aes.Create();
            AesEncryption.Padding = PaddingMode.None; // We put it in our own blocks
            var hash = SHA256.Create().ComputeHash(encryptionKey);
            AesEncryption.Key = hash;

            if (includeRepairData) // We will divide each data block into multiple shards, and generate additional parity shards
            {
                RepairSize = RawBlockSize + IVSize + LenFieldSize;
                if (RepairSize % RepairShardDataCount != 0)
                    RepairSize = RepairSize + (RepairShardDataCount - RepairSize % RepairShardDataCount);
                 
                RepairChecksumSize = (ChecksumSize * (RepairShardDataCount + RepairShardRepairCount));
                RepairSize = ((RawBlockSize / RepairShardDataCount) * RepairShardRepairCount) + RepairChecksumSize * 2;
            }

            HeaderSize = LenFieldSize + IVSize + ChecksumSize + RepairSize;
            FullBlockSize = HeaderSize + RawBlockSize;

            InnerPosition = innerStream.Position;
            InnerLength = innerStream.Length;

            RawPosition = innerStream.Position; // TODO

            // TODO: We are now just estimating the length
            RawLength = innerStream.Length / FullBlockSize * RawBlockSize;
            if (innerStream.Length % FullBlockSize > HeaderSize)
                RawLength += innerStream.Length % FullBlockSize - HeaderSize;

            RawBlockBuffer = new byte[RawBlockSize];
            FullBlockBuffer = new byte[FullBlockSize];
        }

        public override bool CanRead => InnerStream?.CanRead ?? false;

        public override bool CanSeek => InnerStream?.CanSeek ?? false;

        public override bool CanWrite => InnerStream?.CanWrite ?? false;

        public override long Length => RawLength;

        public override long Position
        {
            get { return RawPosition; }
            set { RawPosition = value; }
        }

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
            var startBlockPos = FullBlockSize * CurrentBlockIndex;
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

            if (CurrentBlockLength < RawBlockSize) // last block, update the length to the exact number
                RawLength = CurrentBlockIndex * RawBlockSize + CurrentBlockLength;

            return repaired;
        }


        private void WriteBlockFromBuffer()
        {
            if (!CurrentBlockBufferWritten || InnerStream == null)
                return;

            var startBlockPos = FullBlockSize * CurrentBlockIndex;
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
                if (CurrentBlockLength < RawBlockSize)
                    return repairedCount;
                CurrentBlockIndex++;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (InnerStream == null)
                return 0;

            int len = 0;
            while (count > 0 && RawPosition < RawLength)
            {
                var readInBlock = (int)(RawPosition / RawBlockSize);
                var readInBlockPos = (int)(RawPosition % RawBlockSize);
                if (readInBlock != CurrentBlockIndex)
                {
                    WriteBlockFromBuffer(); // Write any previous cached block

                    CurrentBlockIndex = readInBlock;
                    ReadBlockToBuffer();
                }

                var readLength = count < RawBlockSize - readInBlockPos ? count : RawBlockSize - readInBlockPos;
                if (readLength + RawPosition > RawLength)
                    readLength = (int)(RawLength - RawPosition);
                Buffer.BlockCopy(RawBlockBuffer, readInBlockPos, buffer, offset, readLength);

                len += readLength;
                count -= readLength;
                offset += readLength;
                RawPosition += readLength;
            }
            return len;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (InnerStream == null)
                return 0;
            // Calculate inner position
            var writeInBlock = (int)(RawPosition / RawBlockSize);
            var writeInBlockPos = (int)(RawPosition % RawBlockSize);

            if (writeInBlock != CurrentBlockIndex)
            {
                WriteBlockFromBuffer(); // Write any previous cached block

                CurrentBlockIndex = writeInBlock;
                ReadBlockToBuffer();
            }

            RawPosition = offset;


            InnerPosition = writeInBlock * FullBlockSize;
            InnerPosition += HeaderSize + writeInBlockPos;

            return InnerStream.Seek(InnerPosition, origin);
        }

        public override void SetLength(long value)
        {
            // TODO
            //InnerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (InnerStream == null)
                return;
            while (count > 0)
            {
                var writeInBlock = (int)(RawPosition / RawBlockSize);
                var writeInBlockPos = (int)(RawPosition % RawBlockSize);

                if (writeInBlock != CurrentBlockIndex)
                {
                    WriteBlockFromBuffer(); // Write any previous cached block

                    CurrentBlockIndex = writeInBlock;
                    if (RawLength > RawPosition && (count < RawBlockSize || writeInBlockPos > 0)) // Partial overwriting, load current data
                    {
                        ReadBlockToBuffer();
                    }
                    else
                    {
                        // Overwriting a full block, or writing to a new one
                        CurrentBlockLength = 0;
                    }
                }

                var writeLength = count < RawBlockSize - writeInBlockPos ? count : RawBlockSize - writeInBlockPos;
                Buffer.BlockCopy(buffer, offset, RawBlockBuffer, writeInBlockPos, writeLength);
                CurrentBlockBufferWritten = true;

                if (CurrentBlockLength < writeInBlockPos + writeLength)
                    CurrentBlockLength = writeInBlockPos + writeLength;

                count -= writeLength;
                offset += writeLength;
                RawPosition += writeLength;
                if (RawLength < RawPosition)
                    RawLength = RawPosition;
            }
        }
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
