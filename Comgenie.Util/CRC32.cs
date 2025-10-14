using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Util
{
    internal class CRC32
    {
        public static uint CalculateCRC32(byte[] data, int offset, int len)
        {
            uint[] table = new uint[256];
            uint polynomial = 0xEDB88320;
            uint initialValue = 0xFFFFFFFF;

            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((value & 0x00000001) == 1)
                        value = (value >> 1) ^ polynomial;
                    else
                        value >>= 1;
                }
                table[i] = value;
            }

            uint crc = initialValue;
            for (int i = offset; i < data.Length && len > 0; i++)
            {
                byte index = (byte)((crc ^ data[i]) & 0xFF);
                crc = (crc >> 8) ^ table[index];
                len--;
            }
            return ~crc;
        }
    }
}
