using System;
using System.IO;
using System.Text;

namespace FastbootEnhance.Core.Payload
{
    public readonly struct ZipEntryLocation
    {
        public string Name { get; }

        /// <summary>Absolute offset in the zip file where the entry's bytes begin.</summary>
        public long DataOffset { get; }

        public long CompressedSize { get; }

        public long UncompressedSize { get; }

        /// <summary>True when the entry is stored uncompressed and can be read in place.</summary>
        public bool IsStored { get; }

        public ZipEntryLocation(
            string name, long dataOffset, long compressedSize, long uncompressedSize, bool isStored)
        {
            Name = name;
            DataOffset = dataOffset;
            CompressedSize = compressedSize;
            UncompressedSize = uncompressedSize;
            IsStored = isStored;
        }
    }

    /// <summary>
    /// Finds an entry inside a zip without inflating it, so a stored payload.bin can be read
    /// straight out of an OTA package. ZIP64 is handled because OTA packages pass 4 GB.
    /// </summary>
    public static class ZipEntryLocator
    {
        const uint EocdSignature = 0x06054b50;
        const uint Zip64LocatorSignature = 0x07064b50;
        const uint Zip64EocdSignature = 0x06064b50;
        const uint CentralEntrySignature = 0x02014b50;
        const uint LocalHeaderSignature = 0x04034b50;

        const int EocdFixedSize = 22;
        const int Zip64LocatorSize = 20;
        const int CentralEntryFixedSize = 46;
        const int LocalHeaderFixedSize = 30;

        /// <summary>Returns the named entry, or null when the zip has no such entry.</summary>
        public static ZipEntryLocation? Find(Stream zip, string entryName)
        {
            if (zip == null) throw new ArgumentNullException(nameof(zip));
            if (!zip.CanSeek) throw new ArgumentException("zip stream must be seekable", nameof(zip));

            long eocd = FindEocd(zip);
            if (eocd < 0)
                throw new PayloadFormatException("not a zip file: no end-of-central-directory record");

            zip.Position = eocd + 10;
            int totalEntries = ReadUInt16(zip);
            zip.Position = eocd + 16;
            long centralOffset = ReadUInt32(zip);

            // All-ones values usually mean "see the ZIP64 record", but they are also legal
            // literal values, so only switch to ZIP64 when its locator is actually there.
            if ((centralOffset == uint.MaxValue || totalEntries == ushort.MaxValue) && HasZip64Locator(zip, eocd))
            {
                ReadZip64Tail(zip, eocd, ref centralOffset, ref totalEntries);
            }

            zip.Position = centralOffset;
            for (int i = 0; i < totalEntries; i++)
            {
                if (ReadUInt32(zip) != CentralEntrySignature)
                    throw new PayloadFormatException("damaged zip central directory");

                long entryStart = zip.Position - 4;

                zip.Position = entryStart + 10;
                int method = ReadUInt16(zip);

                zip.Position = entryStart + 20;
                long compressedSize = ReadUInt32(zip);
                long uncompressedSize = ReadUInt32(zip);
                int nameLength = ReadUInt16(zip);
                int extraLength = ReadUInt16(zip);
                int commentLength = ReadUInt16(zip);

                zip.Position = entryStart + 42;
                long localOffset = ReadUInt32(zip);

                byte[] nameBytes = ReadExactly(zip, nameLength);
                string name = Encoding.UTF8.GetString(nameBytes);

                byte[] extra = ReadExactly(zip, extraLength);
                ApplyZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localOffset);

                zip.Position += commentLength;

                if (!string.Equals(name, entryName, StringComparison.Ordinal))
                    continue;

                long dataOffset = ResolveDataOffset(zip, localOffset, name);
                return new ZipEntryLocation(name, dataOffset, compressedSize, uncompressedSize, method == 0);
            }

            return null;
        }

        static long ResolveDataOffset(Stream zip, long localOffset, string name)
        {
            zip.Position = localOffset;
            if (ReadUInt32(zip) != LocalHeaderSignature)
                throw new PayloadFormatException("damaged zip local header for " + name);

            zip.Position = localOffset + 26;
            int nameLength = ReadUInt16(zip);
            int extraLength = ReadUInt16(zip);
            return localOffset + LocalHeaderFixedSize + nameLength + extraLength;
        }

        static bool HasZip64Locator(Stream zip, long eocd)
        {
            long locator = eocd - Zip64LocatorSize;
            if (locator < 0)
                return false;
            zip.Position = locator;
            return ReadUInt32(zip) == Zip64LocatorSignature;
        }

        static void ReadZip64Tail(Stream zip, long eocd, ref long centralOffset, ref int totalEntries)
        {
            long locator = eocd - Zip64LocatorSize;
            if (locator < 0)
                throw new PayloadFormatException("zip claims ZIP64 but has no ZIP64 locator");

            zip.Position = locator;
            if (ReadUInt32(zip) != Zip64LocatorSignature)
                throw new PayloadFormatException("zip claims ZIP64 but its locator signature is wrong");

            zip.Position = locator + 8;
            long zip64Eocd = ReadInt64(zip);

            zip.Position = zip64Eocd;
            if (ReadUInt32(zip) != Zip64EocdSignature)
                throw new PayloadFormatException("damaged ZIP64 end-of-central-directory record");

            zip.Position = zip64Eocd + 32;
            long entries = ReadInt64(zip);
            zip.Position = zip64Eocd + 48;
            centralOffset = ReadInt64(zip);

            if (entries < 0 || entries > int.MaxValue)
                throw new PayloadFormatException("zip entry count out of range");
            totalEntries = (int)entries;
        }

        /// <summary>
        /// The ZIP64 extra field restates only the fields that overflowed, in a fixed order,
        /// so each is consumed only when its 32-bit counterpart is all ones.
        /// </summary>
        static void ApplyZip64Extra(
            byte[] extra, ref long uncompressedSize, ref long compressedSize, ref long localOffset)
        {
            int pos = 0;
            while (pos + 4 <= extra.Length)
            {
                int id = extra[pos] | (extra[pos + 1] << 8);
                int size = extra[pos + 2] | (extra[pos + 3] << 8);
                int body = pos + 4;
                if (body + size > extra.Length)
                    break;

                if (id == 0x0001)
                {
                    int cursor = body;
                    if (uncompressedSize == uint.MaxValue && cursor + 8 <= body + size)
                    {
                        uncompressedSize = BitConverter.ToInt64(extra, cursor);
                        cursor += 8;
                    }
                    if (compressedSize == uint.MaxValue && cursor + 8 <= body + size)
                    {
                        compressedSize = BitConverter.ToInt64(extra, cursor);
                        cursor += 8;
                    }
                    if (localOffset == uint.MaxValue && cursor + 8 <= body + size)
                    {
                        localOffset = BitConverter.ToInt64(extra, cursor);
                    }
                    return;
                }

                pos = body + size;
            }
        }

        static long FindEocd(Stream zip)
        {
            long length = zip.Length;
            if (length < EocdFixedSize)
                return -1;

            // The record sits at the very end unless a trailing comment pushes it back,
            // and a comment is at most 64 KiB.
            int window = (int)Math.Min(length, EocdFixedSize + 0xFFFF);
            long windowStart = length - window;

            zip.Position = windowStart;
            byte[] tail = ReadExactly(zip, window);

            for (int i = window - EocdFixedSize; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    int commentLength = tail[i + 20] | (tail[i + 21] << 8);
                    if (i + EocdFixedSize + commentLength == window)
                        return windowStart + i;
                }
            }

            return -1;
        }

        static byte[] ReadExactly(Stream stream, int count)
        {
            return StreamUtil.ReadExactly(stream, count, "unexpected end of zip file");
        }

        static int ReadUInt16(Stream stream)
        {
            byte[] b = ReadExactly(stream, 2);
            return b[0] | (b[1] << 8);
        }

        static long ReadUInt32(Stream stream)
        {
            byte[] b = ReadExactly(stream, 4);
            return (long)(uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        static long ReadInt64(Stream stream)
        {
            return BitConverter.ToInt64(ReadExactly(stream, 8), 0);
        }
    }
}
