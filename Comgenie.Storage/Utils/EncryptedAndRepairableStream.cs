using Comgenie.Storage.Utils.ReedSolomon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Utils
{
    // TODO: Change to use https://github.com/egbakou/reedsolomon/tree/main  instead

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
        private int FullBlockSize = 0;
        private int HeaderSize = 0;
        private Stream InnerStream { get; set; } // stream of encrypted and repairable file

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
        public EncryptedAndRepairableStream(Stream innerStream, byte[] encryptionKey, double? repairPercent)
        {
            InnerStream = innerStream;
            AesEncryption = Aes.Create();
            AesEncryption.Padding = PaddingMode.None; // We put it in our own blocks
            var hash = SHA256.Create().ComputeHash(encryptionKey);
            AesEncryption.Key = hash;

            if (repairPercent.HasValue)
                RepairSize = (int)((double)RawBlockSize / 100 * repairPercent.Value);
            if (RepairSize > 256)
                RepairSize = 256;

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

                var eccData = new byte[RepairSize];
                Buffer.BlockCopy(FullBlockBuffer, ChecksumSize, eccData, 0, RepairSize);

                var repairedData = ReedSolomonAlgorithm.Decode(FullBlockBuffer, eccData, ErrorCorrectionCodeType.QRCode, ChecksumSize + RepairSize, corruptDataLength);
                if (repairedData == null)
                    throw new Exception("Could not repair data in file");
                Buffer.BlockCopy(repairedData, 0, FullBlockBuffer, ChecksumSize + RepairSize, corruptDataLength);

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
                var repairBytes = ReedSolomonAlgorithm.Encode(FullBlockBuffer, RepairSize, ErrorCorrectionCodeType.QRCode, ChecksumSize + RepairSize, CurrentBlockLength + IVSize + LenFieldSize);
                Buffer.BlockCopy(repairBytes, 0, FullBlockBuffer, ChecksumSize, RepairSize);
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
