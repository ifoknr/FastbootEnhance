using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using ChromeosUpdateEngine;
using Google.Protobuf;
using Joveler.Compression.XZ;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace FastbootEnhance.Core.Tests
{
    public readonly struct BlockRange
    {
        public ulong Start { get; }
        public ulong Count { get; }

        public BlockRange(ulong start, ulong count)
        {
            Start = start;
            Count = count;
        }
    }

    /// <summary>One install operation to put in a generated payload.</summary>
    public sealed class OpSpec
    {
        public InstallOperation.Types.Type Type { get; set; }

        /// <summary>The uncompressed bytes this operation contributes. Null for ZERO/DISCARD.</summary>
        public byte[] Content { get; set; }

        public BlockRange[] Dst { get; set; } = Array.Empty<BlockRange>();

        /// <summary>Leave the per-operation data hash out of the manifest.</summary>
        public bool OmitHash { get; set; }

        /// <summary>Flip one byte of the stored blob, to exercise hash failure paths.</summary>
        public bool CorruptBlob { get; set; }
    }

    public sealed class PartSpec
    {
        public string Name { get; set; }

        /// <summary>The image the operations are expected to reproduce, byte for byte.</summary>
        public byte[] ExpectedImage { get; set; }

        public List<OpSpec> Ops { get; } = new List<OpSpec>();

        public bool OmitSize { get; set; }
        public bool OmitHash { get; set; }

        /// <summary>Put a deliberately wrong image hash in the manifest.</summary>
        public bool WrongHash { get; set; }
    }

    /// <summary>
    /// Writes real CrAU v2 payloads so the reader is tested against the format rather than
    /// against a mock. Compression here uses independent encoders: liblzma for xz, SharpCompress
    /// for bzip2 and ZstdSharp for zstd.
    /// </summary>
    public sealed class TestPayloadBuilder
    {
        static readonly object XzInitLock = new object();
        static bool xzReady;

        readonly List<PartSpec> parts = new List<PartSpec>();

        public uint BlockSize { get; set; } = 4096;
        public uint MinorVersion { get; set; } = 9;
        public ulong FormatVersion { get; set; } = 2;

        /// <summary>Last chance to fill in manifest fields the builder does not set itself.</summary>
        public Action<DeltaArchiveManifest> CustomizeManifest { get; set; }

        public PartSpec AddPartition(string name, byte[] expectedImage)
        {
            PartSpec spec = new PartSpec { Name = name, ExpectedImage = expectedImage };
            parts.Add(spec);
            return spec;
        }

        public byte[] Build()
        {
            using (MemoryStream blobs = new MemoryStream())
            {
                DeltaArchiveManifest manifest = new DeltaArchiveManifest
                {
                    BlockSize = BlockSize,
                    MinorVersion = MinorVersion
                };

                foreach (PartSpec part in parts)
                {
                    PartitionUpdate update = new PartitionUpdate { PartitionName = part.Name };

                    foreach (OpSpec op in part.Ops)
                    {
                        byte[] blob = Encode(op);
                        long offset = blobs.Position;

                        // The manifest hash covers the intended bytes, so corruption is applied
                        // only to what lands in the file. That is what a damaged download looks like.
                        byte[] stored = blob;
                        if (op.CorruptBlob && blob.Length > 0)
                        {
                            stored = (byte[])blob.Clone();
                            stored[stored.Length / 2] ^= 0xFF;
                        }
                        blobs.Write(stored, 0, stored.Length);

                        InstallOperation operation = new InstallOperation
                        {
                            Type = op.Type,
                            DataOffset = (ulong)offset,
                            DataLength = (ulong)blob.Length
                        };

                        if (!op.OmitHash && blob.Length > 0)
                        {
                            using (SHA256 sha = SHA256.Create())
                                operation.DataSha256Hash = ByteString.CopyFrom(sha.ComputeHash(blob));
                        }

                        foreach (BlockRange range in op.Dst)
                        {
                            operation.DstExtents.Add(new Extent
                            {
                                StartBlock = range.Start,
                                NumBlocks = range.Count
                            });
                        }

                        update.Operations.Add(operation);
                    }

                    PartitionInfo info = new PartitionInfo();
                    if (!part.OmitSize)
                        info.Size = (ulong)part.ExpectedImage.Length;

                    if (!part.OmitHash)
                    {
                        using (SHA256 sha = SHA256.Create())
                        {
                            byte[] hash = sha.ComputeHash(part.ExpectedImage);
                            if (part.WrongHash)
                                hash[0] ^= 0xFF;
                            info.Hash = ByteString.CopyFrom(hash);
                        }
                    }

                    if (!part.OmitSize || !part.OmitHash)
                        update.NewPartitionInfo = info;

                    manifest.Partitions.Add(update);
                }

                CustomizeManifest?.Invoke(manifest);

                byte[] blobBytes = blobs.ToArray();

                Signatures trailing = new Signatures();
                trailing.Signatures_.Add(new Signatures.Types.Signature
                {
                    Data = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 })
                });
                byte[] trailingBytes = trailing.ToByteArray();

                manifest.SignaturesOffset = (ulong)blobBytes.Length;
                manifest.SignaturesSize = (ulong)trailingBytes.Length;

                byte[] manifestBytes = manifest.ToByteArray();
                byte[] metadataSignature = new Signatures().ToByteArray();

                using (MemoryStream file = new MemoryStream())
                {
                    file.Write(new[] { (byte)'C', (byte)'r', (byte)'A', (byte)'U' }, 0, 4);
                    WriteUInt64BigEndian(file, FormatVersion);
                    WriteUInt64BigEndian(file, (ulong)manifestBytes.Length);
                    WriteUInt32BigEndian(file, (uint)metadataSignature.Length);
                    file.Write(manifestBytes, 0, manifestBytes.Length);
                    file.Write(metadataSignature, 0, metadataSignature.Length);
                    file.Write(blobBytes, 0, blobBytes.Length);
                    file.Write(trailingBytes, 0, trailingBytes.Length);
                    return file.ToArray();
                }
            }
        }

        public string WriteToFile(string directory, string fileName = "payload.bin")
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, Build());
            return path;
        }

        /// <summary>Wraps the payload in an OTA-shaped zip next to a couple of sibling entries.</summary>
        public string WriteToZip(string directory, string fileName, bool stored)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            byte[] payload = Build();

            CompressionLevel level = stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "META-INF/com/android/metadata", "ota-type=AB\n", CompressionLevel.Optimal);
                WriteEntry(archive, "care_map.pb", "not-a-real-care-map", CompressionLevel.Optimal);

                ZipArchiveEntry entry = archive.CreateEntry("payload.bin", level);
                using (Stream target = entry.Open())
                    target.Write(payload, 0, payload.Length);

                WriteEntry(archive, "payload_properties.txt", "FILE_HASH=0\n", CompressionLevel.Optimal);
            }

            return path;
        }

        static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel level)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, level);
            using (Stream target = entry.Open())
            using (StreamWriter writer = new StreamWriter(target))
                writer.Write(content);
        }

        byte[] Encode(OpSpec op)
        {
            byte[] blob;

            switch (op.Type)
            {
                case InstallOperation.Types.Type.Zero:
                case InstallOperation.Types.Type.Discard:
                    return Array.Empty<byte>();

                case InstallOperation.Types.Type.Replace:
                    blob = (byte[])op.Content.Clone();
                    break;

                case InstallOperation.Types.Type.ReplaceBz:
                    using (MemoryStream output = new MemoryStream())
                    {
                        using (BZip2Stream bz = new BZip2Stream(output, SharpCompress.Compressors.CompressionMode.Compress, false))
                            bz.Write(op.Content, 0, op.Content.Length);
                        blob = output.ToArray();
                    }
                    break;

                case InstallOperation.Types.Type.ReplaceXz:
                    EnsureXz();
                    using (MemoryStream output = new MemoryStream())
                    {
                        XZCompressOptions options = new XZCompressOptions
                        {
                            Level = LzmaCompLevel.Level6,
                            LeaveOpen = true
                        };
                        using (XZStream xz = new XZStream(output, options))
                            xz.Write(op.Content, 0, op.Content.Length);
                        blob = output.ToArray();
                    }
                    break;

                case InstallOperation.Types.Type.Zstd:
                    using (MemoryStream output = new MemoryStream())
                    {
                        using (ZstdSharp.CompressionStream zstd = new ZstdSharp.CompressionStream(output, 7))
                            zstd.Write(op.Content, 0, op.Content.Length);
                        blob = output.ToArray();
                    }
                    break;

                default:
                    // Operation types the reader must refuse still need some bytes on disk.
                    blob = op.Content != null ? (byte[])op.Content.Clone() : new byte[] { 0xDE, 0xAD };
                    break;
            }

            return blob;
        }

        internal static void EnsureXz()
        {
            lock (XzInitLock)
            {
                if (xzReady)
                    return;

                string runtime = Environment.OSVersion.Platform == PlatformID.Win32NT ? "win-x64" : "linux-x64";
                string name = Environment.OSVersion.Platform == PlatformID.Win32NT ? "liblzma.dll" : "liblzma.so";
                string native = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime, "native", name);

                XZInit.GlobalInit(native);
                xzReady = true;
            }
        }

        static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        static void WriteUInt64BigEndian(Stream stream, ulong value)
        {
            for (int shift = 56; shift >= 0; shift -= 8)
                stream.WriteByte((byte)(value >> shift));
        }
    }
}
