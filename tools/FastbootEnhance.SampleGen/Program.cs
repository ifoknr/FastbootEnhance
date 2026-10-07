using ChromeosUpdateEngine;
using FastbootEnhance.Core.Tests;

namespace FastbootEnhance.SampleGen
{
    /// <summary>
    /// Writes an OTA zip shaped like a real full A/B package: familiar partition names,
    /// 2 MB operations, mostly zstd with a few xz, bzip2 and raw partitions, zeroed tails,
    /// and dynamic partition metadata. The data is synthetic; it exists for screenshots and
    /// for exercising the app end to end without a real ROM.
    /// </summary>
    static class Program
    {
        const uint BlockSize = 4096;
        const int OpBytes = 2 * 1024 * 1024;

        static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : "sample";

            var layout = new (string name, int kilobytes, InstallOperation.Types.Type codec, bool dynamic)[]
            {
                ("boot",          64 * 1024, InstallOperation.Types.Type.Zstd,      false),
                ("init_boot",      8 * 1024, InstallOperation.Types.Type.Zstd,      false),
                ("vendor_boot",   64 * 1024, InstallOperation.Types.Type.Zstd,      false),
                ("dtbo",           8 * 1024, InstallOperation.Types.Type.ReplaceXz, false),
                ("vbmeta",               64, InstallOperation.Types.Type.Replace,   false),
                ("vbmeta_system",         8, InstallOperation.Types.Type.Replace,   false),
                ("system",       256 * 1024, InstallOperation.Types.Type.Zstd,      true),
                ("system_ext",    96 * 1024, InstallOperation.Types.Type.Zstd,      true),
                ("product",      128 * 1024, InstallOperation.Types.Type.Zstd,      true),
                ("vendor",        96 * 1024, InstallOperation.Types.Type.Zstd,      true),
                ("vendor_dlkm",   32 * 1024, InstallOperation.Types.Type.Zstd,      true),
                ("odm",            8 * 1024, InstallOperation.Types.Type.ReplaceBz, true),
            };

            TestPayloadBuilder builder = new TestPayloadBuilder
            {
                BlockSize = BlockSize,
                MinorVersion = 0, // full packages report 0
                CustomizeManifest = manifest =>
                {
                    manifest.MaxTimestamp = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
                    manifest.SecurityPatchLevel = "2026-09-05";
                    manifest.PartialUpdate = false;

                    DynamicPartitionGroup group = new DynamicPartitionGroup
                    {
                        Name = "main",
                        Size = 9UL * 1024 * 1024 * 1024
                    };
                    foreach (var entry in layout)
                        if (entry.dynamic)
                            group.PartitionNames.Add(entry.name);

                    manifest.DynamicPartitionMetadata = new DynamicPartitionMetadata { SnapshotEnabled = true };
                    manifest.DynamicPartitionMetadata.Groups.Add(group);
                }
            };

            int seed = 1;
            foreach (var entry in layout)
            {
                int size = entry.kilobytes * 1024;
                byte[] image = RomLike(size, seed++);
                PartSpec part = builder.AddPartition(entry.name, image);

                // Real images end in a run of empty blocks, which payloads encode as ZERO.
                int zeroTail = size >= 16 * 1024 * 1024 ? size / 8 / (int)BlockSize * (int)BlockSize : 0;
                Array.Clear(image, size - zeroTail, zeroTail);
                int dataEnd = size - zeroTail;

                for (int offset = 0; offset < dataEnd; offset += OpBytes)
                {
                    int length = Math.Min(OpBytes, dataEnd - offset);
                    byte[] chunk = new byte[length];
                    Buffer.BlockCopy(image, offset, chunk, 0, length);
                    part.Ops.Add(new OpSpec
                    {
                        Type = entry.codec,
                        Content = chunk,
                        Dst = new[] { new BlockRange((ulong)(offset / (int)BlockSize), (ulong)((length + BlockSize - 1) / BlockSize)) }
                    });
                }

                if (zeroTail > 0)
                {
                    part.Ops.Add(new OpSpec
                    {
                        Type = InstallOperation.Types.Type.Zero,
                        Dst = new[] { new BlockRange((ulong)(dataEnd / (int)BlockSize), (ulong)(zeroTail / (int)BlockSize)) }
                    });
                }

                Console.WriteLine("built " + entry.name.PadRight(14) + (entry.kilobytes / 1024.0).ToString("F1").PadLeft(7) + " MB  " + entry.codec);
            }

            string zip = builder.WriteToZip(outDir, "sample-ota.zip", stored: true);
            Console.WriteLine("wrote " + zip + " (" + new FileInfo(zip).Length / 1024 / 1024 + " MB)");

            WriteSuper(outDir);
            return 0;
        }

        /// <summary>
        /// A sparse super image split in parts, like a factory image's super.img_sparsechunk.N,
        /// with a file of the expected SHA-256 of each partition and of the expanded image, so a
        /// test can check what the app extracts.
        /// </summary>
        static void WriteSuper(string outDir)
        {
            var partitions = new (string name, int megabytes, bool erofs)[]
            {
                ("system_a", 48, true), ("system_ext_a", 16, true), ("product_a", 24, true),
                ("vendor_a", 24, false), ("odm_a", 4, true),
            };
            FastbootEnhance.Core.Images.SuperBuilder builder = new FastbootEnhance.Core.Images.SuperBuilder { HeaderFlags = 1 };
            builder.AddGroup("qti_dynamic_partitions_a", 240UL << 20);
            builder.AddGroup("qti_dynamic_partitions_b", 240UL << 20);
            List<string> expected = new List<string>();
            int seed = 100;
            foreach (var p in partitions)
            {
                byte[] data = RomLike(p.megabytes << 20, seed++);
                if (p.erofs)
                    BitConverter.GetBytes(0xE0F5E1E2u).CopyTo(data, 1024);
                else
                    BitConverter.GetBytes((ushort)0xEF53).CopyTo(data, 1024 + 56);
                builder.AddPartition(p.name, "qti_dynamic_partitions_a", new MemoryStream(data));
                builder.AddPartition(p.name.Replace("_a", "_b"), "qti_dynamic_partitions_b", null);
                expected.Add(Sha256Hex(data) + "  " + p.name + ".img");
            }

            string raw = Path.Combine(outDir, "super-sample.raw");
            using (FileStream file = new FileStream(raw, FileMode.Create, FileAccess.ReadWrite))
                builder.Write(file, 256L << 20);
            expected.Add(Sha256Hex(File.ReadAllBytes(raw)) + "  super.raw.img");

            FastbootEnhance.Core.Images.SparseWriteResult sparse = FastbootEnhance.Core.Images.SparseConverter.ToSparse(
                raw, Path.Combine(outDir, "super.img_sparsechunk"), 4096, 40L << 20, null, System.Threading.CancellationToken.None);
            File.Delete(raw);
            File.WriteAllLines(Path.Combine(outDir, "super-expected.sha256"), expected);
            Console.WriteLine("wrote super.img_sparsechunk.0.." + (sparse.Files.Count - 1) + " (" + sparse.Bytes / 1024 / 1024 + " MB)");
        }

        static string Sha256Hex(byte[] data)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        /// <summary>A quarter of each block random, the rest zero: compresses about 4:1, like system images.</summary>
        static byte[] RomLike(int size, int seed)
        {
            byte[] data = new byte[size];
            Random random = new Random(seed);
            byte[] noise = new byte[BlockSize / 4];
            for (int offset = 0; offset < size; offset += (int)BlockSize)
            {
                random.NextBytes(noise);
                Buffer.BlockCopy(noise, 0, data, offset, Math.Min(noise.Length, size - offset));
            }
            return data;
        }
    }
}
