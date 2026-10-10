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
            WriteRecoveryImage(outDir);
            WriteGkiBootImage(outDir);
            return 0;
        }

        /// <summary>
        /// A GKI-style boot image (header v4) the way the Image Tools page and the flash check
        /// read it: an uncompressed arm64 kernel with its version banner (android13-5.15), a
        /// gzip ramdisk patched by Magisk, and an AVB footer carrying the security patch.
        /// </summary>
        static void WriteGkiBootImage(string outDir)
        {
            System.Text.Encoding ascii = System.Text.Encoding.ASCII;
            MemoryStream kernel = new MemoryStream();
            byte[] imageHeader = new byte[64];
            ascii.GetBytes("ARMd").CopyTo(imageHeader, 56);
            kernel.Write(imageHeader, 0, imageHeader.Length);
            byte[] banner = ascii.GetBytes("Linux version 5.15.123-android13-8-00001-gfeed01 (builder@sample) #1 SMP PREEMPT\n\0");
            kernel.Write(banner, 0, banner.Length);
            kernel.Write(new byte[64 << 10], 0, 64 << 10);

            MemoryStream cpio = new MemoryStream();
            void Entry(string name)
            {
                byte[] n = ascii.GetBytes(name + "\0");
                string head = "070701" + "00000001" + (name == "TRAILER!!!" ? "00000000" : "000081A4") + "00000000" + "00000000"
                    + "00000001" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000"
                    + n.Length.ToString("X8") + "00000000";
                cpio.Write(ascii.GetBytes(head), 0, 110);
                cpio.Write(n, 0, n.Length);
                while (cpio.Length % 4 != 0)
                    cpio.WriteByte(0);
            }
            foreach (string name in new[] { "init", ".backup/.magisk", ".backup/init", "overlay.d/sbin/magisk64.xz", "TRAILER!!!" })
                Entry(name);
            MemoryStream ramdisk = new MemoryStream();
            using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(ramdisk, System.IO.Compression.CompressionLevel.Optimal, true))
                cpio.WriteTo(gz);

            MemoryStream image = new MemoryStream();
            byte[] header = new byte[4096];
            ascii.GetBytes("ANDROID!").CopyTo(header, 0);
            BitConverter.GetBytes((uint)kernel.Length).CopyTo(header, 8);
            BitConverter.GetBytes((uint)ramdisk.Length).CopyTo(header, 12);
            BitConverter.GetBytes(4096u).CopyTo(header, 20);  // header_size
            BitConverter.GetBytes(4u).CopyTo(header, 40);     // header_version
            image.Write(header, 0, header.Length);
            void Padded(MemoryStream part)
            {
                part.WriteTo(image);
                while (image.Length % 4096 != 0)
                    image.WriteByte(0);
            }
            Padded(kernel);
            Padded(ramdisk);

            long vbmetaOffset = image.Length;
            MemoryStream vbmeta = new MemoryStream();
            vbmeta.Write(ascii.GetBytes("AVB0"), 0, 4);
            vbmeta.Write(new byte[252], 0, 252);
            foreach (string property in new[] { "com.android.build.boot.os_version\013", "com.android.build.boot.security_patch\02024-05-05" })
            {
                vbmeta.Write(new byte[32], 0, 32);
                byte[] kv = ascii.GetBytes(property + "\0");
                vbmeta.Write(kv, 0, kv.Length);
            }
            vbmeta.WriteTo(image);
            while (image.Length % 4096 != 0)
                image.WriteByte(0);
            byte[] footer = new byte[64];
            ascii.GetBytes("AVBf").CopyTo(footer, 0);
            for (int i = 0; i < 8; i++)
            {
                footer[20 + i] = (byte)(vbmetaOffset >> (56 - 8 * i));
                footer[28 + i] = (byte)(vbmeta.Length >> (56 - 8 * i));
            }
            image.Write(footer, 0, footer.Length);

            string path = Path.Combine(outDir, "boot-gki-sample.img");
            File.WriteAllBytes(path, image.ToArray());
            Console.WriteLine("wrote " + path);
        }

        /// <summary>
        /// A stand-in for a TWRP image: just the boot image magic and a page-sized header, which
        /// is all the app checks before handing the file to "fastboot boot".
        /// </summary>
        static void WriteRecoveryImage(string outDir)
        {
            byte[] image = new byte[2 << 20];
            System.Text.Encoding.ASCII.GetBytes("ANDROID!").CopyTo(image, 0);
            BitConverter.GetBytes(1u << 20).CopyTo(image, 8);   // kernel_size: it has a kernel
            BitConverter.GetBytes(4096u).CopyTo(image, 36);     // page_size (header v0)
            string path = Path.Combine(outDir, "twrp-sample.img");
            File.WriteAllBytes(path, image);
            Console.WriteLine("wrote " + path);

            // An init_boot image (header v4, ramdisk only), which cannot be booted on its own.
            byte[] initBoot = new byte[64 << 10];
            System.Text.Encoding.ASCII.GetBytes("ANDROID!").CopyTo(initBoot, 0);
            BitConverter.GetBytes(32768u).CopyTo(initBoot, 12);  // ramdisk_size
            BitConverter.GetBytes(4096u).CopyTo(initBoot, 20);   // header_size
            BitConverter.GetBytes(4u).CopyTo(initBoot, 40);      // header_version
            string initPath = Path.Combine(outDir, "init_boot-sample.img");
            File.WriteAllBytes(initPath, initBoot);
            Console.WriteLine("wrote " + initPath);

            // A sparse image of 12 GiB that is one "don't care" chunk: 40 bytes on disk, far
            // more than the stand-in phone's super can take once written.
            const uint blockSize = 4096;
            const uint blocks = (uint)((12L << 30) / blockSize);
            using (BinaryWriter big = new BinaryWriter(File.Create(Path.Combine(outDir, "big-sparse.img"))))
            {
                big.Write(0xED26FF3Au); // magic
                big.Write((ushort)1);   // major version
                big.Write((ushort)0);   // minor version
                big.Write((ushort)28);  // file header size
                big.Write((ushort)12);  // chunk header size
                big.Write(blockSize);
                big.Write(blocks);      // total blocks
                big.Write(1u);          // total chunks
                big.Write(0u);          // checksum
                big.Write((ushort)0xCAC3); // DONT_CARE
                big.Write((ushort)0);
                big.Write(blocks);      // chunk size in blocks
                big.Write(12u);         // chunk header only
            }
            Console.WriteLine("wrote big-sparse.img");
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
            WriteQualcommPieces(raw, Path.Combine(outDir, "qualcomm"));
            File.Delete(raw);
            File.WriteAllLines(Path.Combine(outDir, "super-expected.sha256"), expected);
            Console.WriteLine("wrote super.img_sparsechunk.0.." + (sparse.Files.Count - 1) + " (" + sparse.Bytes / 1024 / 1024 + " MB)");
        }

        /// <summary>
        /// The same super as a Qualcomm flash package ships it: super_1.img, super_2.img ... one
        /// per run of data, each listed with its sector in rawprogram_unsparse0.xml.
        /// </summary>
        static void WriteQualcommPieces(string raw, string folder)
        {
            const int block = 4096;
            const long diskStart = 1572870;   // sectors before super on the disk
            Directory.CreateDirectory(folder);
            byte[] image = File.ReadAllBytes(raw);
            int blocks = image.Length / block;
            bool zero(int b)
            {
                for (int i = b * block; i < (b + 1) * block; i++)
                    if (image[i] != 0) return false;
                return true;
            }

            System.Text.StringBuilder xml = new System.Text.StringBuilder("<?xml version=\"1.0\" ?>\n<data>\n");
            int at = 0;
            int piece = 0;
            while (at < blocks)
            {
                if (at > 0 && zero(at))
                {
                    at++;
                    continue;
                }
                int start = at;
                int zeros = 0;
                while (at < blocks && zeros < 64)
                {
                    zeros = zero(at) ? zeros + 1 : 0;
                    at++;
                }
                int end = at - zeros;
                // Packages cap the size of a piece; 64 MB here gives a handful of them.
                for (int first = start; first < end; first += 16384)
                {
                int count = Math.Min(16384, end - first);
                string name = "super_" + (++piece) + ".img";
                using (FileStream file = File.Create(Path.Combine(folder, name)))
                    file.Write(image, first * block, count * block);
                xml.Append("  <program SECTOR_SIZE_IN_BYTES=\"4096\" file_sector_offset=\"0\" filename=\"" + name
                    + "\" label=\"super\" num_partition_sectors=\"" + count + "\" partofsingleimage=\"false\" physical_partition_number=\"0\""
                    + " readbackverify=\"false\" size_in_KB=\"" + (count * 4) + ".0\" sparse=\"false\" start_byte_hex=\"0x"
                    + ((diskStart + first) * block).ToString("x") + "\" start_sector=\"" + (diskStart + first) + "\"/>\n");
                }
            }
            xml.Append("</data>\n");
            File.WriteAllText(Path.Combine(folder, "rawprogram_unsparse0.xml"), xml.ToString());
            Console.WriteLine("wrote qualcomm/super_1.img..super_" + piece + ".img and rawprogram_unsparse0.xml");
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
