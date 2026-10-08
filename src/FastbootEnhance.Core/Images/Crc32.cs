using System;

namespace FastbootEnhance.Core.Images
{
    /// <summary>
    /// The standard CRC-32 (IEEE 802.3, the zlib one) that Android sparse images use for
    /// image_checksum and CRC32 chunks. Slicing-by-8 keeps it near a gigabyte a second.
    /// </summary>
    public sealed class Crc32
    {
        static readonly uint[][] Tables = BuildTables();

        uint state = 0xFFFFFFFFu;

        public uint Value => state ^ 0xFFFFFFFFu;

        static uint[][] BuildTables()
        {
            uint[][] tables = new uint[8][];
            for (int t = 0; t < 8; t++)
                tables[t] = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                tables[0][i] = c;
            }
            for (int i = 0; i < 256; i++)
            {
                for (int t = 1; t < 8; t++)
                    tables[t][i] = (tables[t - 1][i] >> 8) ^ tables[0][tables[t - 1][i] & 0xFF];
            }
            return tables;
        }

        public void Append(byte[] buffer, int offset, int count)
        {
            uint crc = state;
            uint[] t0 = Tables[0], t1 = Tables[1], t2 = Tables[2], t3 = Tables[3],
                   t4 = Tables[4], t5 = Tables[5], t6 = Tables[6], t7 = Tables[7];
            int i = offset;
            int end = offset + count;
            while (end - i >= 8)
            {
                uint one = crc ^ (uint)(buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16 | buffer[i + 3] << 24);
                uint two = (uint)(buffer[i + 4] | buffer[i + 5] << 8 | buffer[i + 6] << 16 | buffer[i + 7] << 24);
                crc = t7[one & 0xFF] ^ t6[(one >> 8) & 0xFF] ^ t5[(one >> 16) & 0xFF] ^ t4[one >> 24]
                    ^ t3[two & 0xFF] ^ t2[(two >> 8) & 0xFF] ^ t1[(two >> 16) & 0xFF] ^ t0[two >> 24];
                i += 8;
            }
            while (i < end)
            {
                crc = t0[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                i++;
            }
            state = crc;
        }

        public static uint Compute(byte[] buffer)
        {
            Crc32 crc = new Crc32();
            crc.Append(buffer, 0, buffer.Length);
            return crc.Value;
        }
    }
}
