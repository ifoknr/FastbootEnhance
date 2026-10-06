using System;
using System.IO;
using ChromeosUpdateEngine;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// Decodes a single install operation's attached data blob. Every codec here is
    /// fully managed, so the app ships no native decompression libraries.
    /// </summary>
    internal static class OperationCodec
    {
        /// <summary>
        /// Decompresses <paramref name="srcLength"/> bytes of <paramref name="src"/> into
        /// <paramref name="dest"/>, which is reset first and left rewound.
        /// </summary>
        internal static void DecodeInto(
            InstallOperation.Types.Type type, byte[] src, int srcLength, MemoryStream dest)
        {
            dest.SetLength(0);

            switch (type)
            {
                case InstallOperation.Types.Type.Replace:
                    dest.Write(src, 0, srcLength);
                    break;

                case InstallOperation.Types.Type.ReplaceBz:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (BZip2Stream bz = new BZip2Stream(raw, CompressionMode.Decompress, false))
                    {
                        bz.CopyTo(dest);
                    }
                    break;

                case InstallOperation.Types.Type.ReplaceXz:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (XZStream xz = new XZStream(raw))
                    {
                        xz.CopyTo(dest);
                    }
                    break;

                case InstallOperation.Types.Type.Zstd:
                    using (MemoryStream raw = new MemoryStream(src, 0, srcLength, false))
                    using (ZstdSharp.DecompressionStream zstd = new ZstdSharp.DecompressionStream(raw))
                    {
                        zstd.CopyTo(dest);
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(type), type, "not a data-carrying replace operation");
            }

            dest.Position = 0;
        }
    }
}
