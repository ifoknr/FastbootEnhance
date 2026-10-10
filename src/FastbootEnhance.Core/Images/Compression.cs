using System;
using System.IO;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace FastbootEnhance.Core.Images
{
    public enum CompressionKind
    {
        None,
        Gzip,
        Lz4Legacy,
        Lz4Frame,
        Zstd,
        Xz,
        Lzma,
        Bzip2,
        Unknown,
    }

    /// <summary>Recognises and undoes the compressions found on kernels and ramdisks.</summary>
    public static class Compression
    {
        /// <summary>
        /// What compresses the data at data[start]. None means it is readable as it is: an
        /// arm64 Image ("ARMd" at 56), a zImage, or a cpio archive.
        /// </summary>
        public static CompressionKind Detect(byte[] data, int start, int length)
        {
            if (length < 4)
                return CompressionKind.Unknown;
            byte a = data[start], b = data[start + 1], c = data[start + 2], d = data[start + 3];
            if (a == 0x1F && (b == 0x8B || b == 0x9E)) return CompressionKind.Gzip;
            uint magic = BitConverter.ToUInt32(data, start);
            if (magic == Lz4.LegacyMagic) return CompressionKind.Lz4Legacy;
            if (magic == Lz4.FrameMagic) return CompressionKind.Lz4Frame;
            if (magic == 0xFD2FB528) return CompressionKind.Zstd;
            if (a == 0xFD && b == '7' && c == 'z' && d == 'X') return CompressionKind.Xz;
            if (a == 0x5D && b == 0 && c == 0) return CompressionKind.Lzma;
            if (a == 'B' && b == 'Z' && c == 'h') return CompressionKind.Bzip2;
            if (a == '0' && b == '7' && c == '0' && d == '7') return CompressionKind.None; // cpio
            if (length >= 60 && data[start + 56] == 'A' && data[start + 57] == 'R' && data[start + 58] == 'M' && data[start + 59] == 'd')
                return CompressionKind.None; // arm64 Image
            if (length >= 40 && BitConverter.ToUInt32(data, start + 36) == 0x016F2818)
                return CompressionKind.None; // arm zImage
            return CompressionKind.Unknown;
        }

        /// <summary>
        /// The data with its compression removed, or null when the compression is not
        /// recognised or is legacy lzma. Throws InvalidDataException on damaged data or output
        /// larger than maxOutput.
        /// </summary>
        public static byte[] Decompress(byte[] data, int start, int length, CompressionKind kind, int maxOutput)
        {
            switch (kind)
            {
                case CompressionKind.None:
                    byte[] copy = new byte[length];
                    Buffer.BlockCopy(data, start, copy, 0, length);
                    return copy;
                case CompressionKind.Gzip:
                    return Gunzip(data, start, length, maxOutput);
                case CompressionKind.Zstd:
                    return Unpack(new MemoryStream(data, start, length, false), s => new ZstdSharp.DecompressionStream(s), maxOutput);
                case CompressionKind.Xz:
                    return Unpack(new MemoryStream(data, start, length, false), s => new XZStream(s), maxOutput);
                case CompressionKind.Bzip2:
                    return Unpack(new MemoryStream(data, start, length, false),
                        s => new BZip2Stream(s, SharpCompress.Compressors.CompressionMode.Decompress, false), maxOutput);
                case CompressionKind.Lz4Legacy:
                case CompressionKind.Lz4Frame:
                    return Lz4.Decompress(data, start, length, maxOutput);
                default:
                    return null;
            }
        }

        static byte[] Unpack(Stream input, Func<Stream, Stream> open, int maxOutput)
        {
            using (input)
            using (Stream unpacked = open(input))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                try
                {
                    while ((read = unpacked.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maxOutput)
                            throw new InvalidDataException("Decompressed data larger than allowed.");
                        output.Write(buffer, 0, read);
                    }
                }
                catch (Exception e) when (!(e is InvalidDataException) || output.Length == 0)
                {
                    if (output.Length == 0)
                        throw new InvalidDataException("The compressed data is damaged: " + e.Message, e);
                    // Trailing bytes after the compressed stream: keep what came before them.
                }
                return output.ToArray();
            }
        }

        static byte[] Gunzip(byte[] data, int start, int length, int maxOutput)
        {
            using (MemoryStream input = new MemoryStream(data, start, length, false))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                try
                {
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > maxOutput)
                            throw new InvalidDataException("Decompressed data larger than allowed.");
                        output.Write(buffer, 0, read);
                    }
                }
                catch (InvalidDataException) when (output.Length > 0)
                {
                    // Bytes after the gzip member (an appended DTB) are not gzip; what came
                    // before them is the kernel.
                }
                return output.ToArray();
            }
        }

        public static string Name(CompressionKind kind)
        {
            switch (kind)
            {
                case CompressionKind.None: return "none";
                case CompressionKind.Gzip: return "gzip";
                case CompressionKind.Lz4Legacy: return "lz4 (legacy)";
                case CompressionKind.Lz4Frame: return "lz4";
                case CompressionKind.Zstd: return "zstd";
                case CompressionKind.Xz: return "xz";
                case CompressionKind.Lzma: return "lzma";
                case CompressionKind.Bzip2: return "bzip2";
                default: return "?";
            }
        }
    }
}
