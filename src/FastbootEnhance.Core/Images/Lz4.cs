using System;
using System.IO;

namespace FastbootEnhance.Core.Images
{
    /// <summary>
    /// LZ4 decompression, in the two containers Android uses: the legacy format of the kernel
    /// build ("lz4 -l", magic 02 21 4C 18), used for kernels and Magisk-patched ramdisks, and
    /// the frame format (magic 04 22 4D 18).
    /// </summary>
    public static class Lz4
    {
        public const uint LegacyMagic = 0x184C2102;
        public const uint FrameMagic = 0x184D2204;

        /// <summary>
        /// Decompresses one LZ4 block into output at outPos and returns the new end. Matches may
        /// reach back to historyStart, so blocks of a linked frame can refer to earlier blocks.
        /// </summary>
        public static int DecodeBlock(byte[] input, int start, int length, byte[] output, int outPos, int outLimit, int historyStart = 0)
        {
            int ip = start, end = start + length, op = outPos;
            while (ip < end)
            {
                int token = input[ip++];
                int literals = token >> 4;
                if (literals == 15)
                {
                    int b;
                    do
                    {
                        if (ip >= end) throw new InvalidDataException("LZ4 block ends inside a length.");
                        b = input[ip++];
                        literals += b;
                    } while (b == 255);
                }
                if (ip + literals > end || op + literals > outLimit)
                    throw new InvalidDataException("LZ4 literals run past the end.");
                Buffer.BlockCopy(input, ip, output, op, literals);
                ip += literals;
                op += literals;
                if (ip >= end)
                    break; // the last sequence has literals only

                if (ip + 2 > end) throw new InvalidDataException("LZ4 block ends inside an offset.");
                int offset = input[ip] | (input[ip + 1] << 8);
                ip += 2;
                if (offset == 0 || offset > op - historyStart)
                    throw new InvalidDataException("LZ4 match points before the start.");

                int match = (token & 15) + 4;
                if ((token & 15) == 15)
                {
                    int b;
                    do
                    {
                        if (ip >= end) throw new InvalidDataException("LZ4 block ends inside a length.");
                        b = input[ip++];
                        match += b;
                    } while (b == 255);
                }
                if (op + match > outLimit)
                    throw new InvalidDataException("LZ4 output larger than allowed.");
                int from = op - offset;
                if (offset >= match)
                {
                    Buffer.BlockCopy(output, from, output, op, match);
                    op += match;
                }
                else
                {
                    // Byte by byte: the match overlaps what it writes (a repeated pattern).
                    for (int i = 0; i < match; i++)
                        output[op++] = output[from + i];
                }
            }
            return op;
        }

        /// <summary>
        /// Decompresses a legacy or frame LZ4 stream that starts at data[start]. Stops at the
        /// end of the stream, so trailing bytes (a DTB after the kernel, padding) are ignored.
        /// Throws when the result would exceed maxOutput.
        /// </summary>
        public static byte[] Decompress(byte[] data, int start, int length, int maxOutput)
        {
            uint magic = BitConverter.ToUInt32(data, start);
            Output output = new Output(maxOutput);
            if (magic == LegacyMagic)
                Legacy(data, start + 4, start + length, output);
            else if (magic == FrameMagic)
                Frame(data, start + 4, start + length, output);
            else
                throw new InvalidDataException("Not LZ4 data.");
            byte[] result = new byte[output.Length];
            Buffer.BlockCopy(output.Buffer, 0, result, 0, output.Length);
            return result;
        }

        /// <summary>All output in one growing buffer, so later blocks can copy from earlier ones.</summary>
        sealed class Output
        {
            readonly int max;
            public byte[] Buffer = new byte[1 << 20];
            public int Length;

            public Output(int max)
            {
                this.max = max;
            }

            /// <summary>Makes room for count more bytes; returns the limit to decode up to.</summary>
            public int Reserve(int count)
            {
                long wanted = Math.Min((long)Length + count, max);
                if (wanted <= Length)
                    throw new InvalidDataException("Decompressed data larger than allowed.");
                if (wanted > Buffer.Length)
                {
                    long size = Math.Min(Math.Max(wanted, (long)Buffer.Length * 2), max);
                    Array.Resize(ref Buffer, (int)size);
                }
                return (int)wanted;
            }
        }

        // Independent blocks of up to 8 MiB, each prefixed by its compressed size; ends at the
        // data's end or at a value that is not a plausible block size (padding, a DTB).
        static void Legacy(byte[] data, int pos, int end, Output output)
        {
            const int blockMax = 8 << 20;
            while (pos + 4 <= end)
            {
                uint size = BitConverter.ToUInt32(data, pos);
                if (size == LegacyMagic)
                {
                    pos += 4; // concatenated legacy streams
                    continue;
                }
                if (size == 0 || size > blockMax + (blockMax / 255) + 16 || pos + 4 + size > end)
                    break;
                pos += 4;
                int limit = output.Reserve(blockMax);
                output.Length = DecodeBlock(data, pos, (int)size, output.Buffer, output.Length, limit, output.Length);
                pos += (int)size;
            }
        }

        static void Frame(byte[] data, int pos, int end, Output output)
        {
            if (pos + 3 > end) throw new InvalidDataException("LZ4 frame header is cut short.");
            int flags = data[pos];
            int bd = data[pos + 1];
            bool independent = (flags & 0x20) != 0;
            bool blockChecksum = (flags & 0x10) != 0;
            bool contentSize = (flags & 0x08) != 0;
            bool dictId = (flags & 0x01) != 0;
            int blockMax = 1 << (8 + 2 * ((bd >> 4) & 7)); // 4: 64 KiB ... 7: 4 MiB
            pos += 2 + (contentSize ? 8 : 0) + (dictId ? 4 : 0) + 1; // + header checksum
            while (pos + 4 <= end)
            {
                uint raw = BitConverter.ToUInt32(data, pos);
                pos += 4;
                if (raw == 0)
                    break; // end mark
                bool stored = (raw & 0x80000000u) != 0;
                int size = (int)(raw & 0x7FFFFFFF);
                if (size > blockMax || pos + size > end)
                    throw new InvalidDataException("LZ4 frame block runs past the end.");
                int limit = output.Reserve(blockMax);
                if (stored)
                {
                    if (output.Length + size > limit)
                        throw new InvalidDataException("Decompressed data larger than allowed.");
                    System.Buffer.BlockCopy(data, pos, output.Buffer, output.Length, size);
                    output.Length += size;
                }
                else
                {
                    output.Length = DecodeBlock(data, pos, size, output.Buffer, output.Length, limit,
                        independent ? output.Length : 0);
                }
                pos += size + (blockChecksum ? 4 : 0);
            }
        }
    }
}
