using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FastbootEnhance.Core.Images;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public sealed class ImageTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "fbe-images-" + Guid.NewGuid().ToString("N"));

        public ImageTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        string P(string name) => Path.Combine(dir, name);

        /// <summary>Random data, zero blocks, two fill patterns and an optional short tail.</summary>
        static byte[] MixedImage(int blocks, int tail, int seed = 1)
        {
            Random random = new Random(seed);
            using (MemoryStream image = new MemoryStream())
            {
                byte[] block = new byte[4096];
                for (int i = 0; i < blocks; i++)
                {
                    switch (i % 11)
                    {
                        case 0: case 1: case 2: case 3:
                            random.NextBytes(block);
                            break;
                        case 4: case 5: case 6:
                            Array.Clear(block, 0, block.Length);
                            break;
                        case 7: case 8:
                            for (int k = 0; k < block.Length; k += 4) BitConverter.GetBytes(0xDEADBEEFu).CopyTo(block, k);
                            break;
                        default:
                            for (int k = 0; k < block.Length; k += 4) BitConverter.GetBytes(0x01020304u).CopyTo(block, k);
                            break;
                    }
                    image.Write(block, 0, block.Length);
                }
                byte[] end = new byte[tail];
                random.NextBytes(end);
                image.Write(end, 0, end.Length);
                return image.ToArray();
            }
        }

        static byte[] Padded(byte[] data, int blockSize = 4096)
        {
            byte[] padded = new byte[(data.Length + blockSize - 1) / blockSize * blockSize];
            Array.Copy(data, padded, data.Length);
            return padded;
        }

        [Fact]
        public void Crc32_matches_the_standard_check_value()
        {
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
            Crc32 split = new Crc32();
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            split.Append(data, 0, 4);
            split.Append(data, 4, 5);
            Assert.Equal(0xCBF43926u, split.Value);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1234)]
        public void Raw_to_sparse_and_back_is_lossless(int tail)
        {
            byte[] raw = MixedImage(300, tail);
            File.WriteAllBytes(P("raw.img"), raw);

            SparseWriteResult written = SparseConverter.ToSparse(P("raw.img"), P("out.simg"), 4096, null, null, CancellationToken.None);
            Assert.Single(written.Files);
            SparseImage sparse = SparseImage.Open(P("out.simg"));
            Assert.True(new FileInfo(P("out.simg")).Length < raw.Length / 2);
            Assert.Equal((raw.Length + 4095) / 4096, sparse.TotalBlocks);
            Assert.True(sparse.CountBlocks(SparseChunkType.Fill) > 100);

            ExpandResult expanded = SparseConverter.ToRaw(new[] { P("out.simg") }, P("back.img"), null, CancellationToken.None);
            Assert.Equal(Padded(raw), File.ReadAllBytes(P("back.img")));
            Assert.Equal(Crc32.Compute(Padded(raw)), expanded.Crc32);
        }

        [Fact]
        public void The_stream_reads_any_range_like_the_expanded_image()
        {
            byte[] raw = MixedImage(200, 0, seed: 9);
            File.WriteAllBytes(P("raw.img"), raw);
            SparseConverter.ToSparse(P("raw.img"), P("s.simg"), 4096, null, null, CancellationToken.None);

            Random random = new Random(3);
            using (SparseStream stream = SparseStream.Open(P("s.simg")))
            {
                Assert.Equal(raw.Length, stream.Length);
                for (int i = 0; i < 200; i++)
                {
                    int offset = random.Next(raw.Length);
                    int count = Math.Min(random.Next(1, 20000), raw.Length - offset);
                    byte[] got = new byte[count];
                    stream.Position = offset;
                    int read = 0;
                    while (read < count)
                        read += stream.Read(got, read, count - read);
                    Assert.True(raw.Skip(offset).Take(count).SequenceEqual(got), "mismatch at " + offset + "+" + count);
                }
            }
        }

        [Fact]
        public void Split_parts_stay_under_the_limit_and_rebuild_the_image()
        {
            byte[] raw = MixedImage(800, 0, seed: 4);
            File.WriteAllBytes(P("raw.img"), raw);
            const long limit = 600_000;

            SparseWriteResult split = SparseConverter.ToSparse(P("raw.img"), P("system.img"), 4096, limit, null, CancellationToken.None);
            Assert.True(split.Files.Count > 1);
            Assert.All(split.Files, f => Assert.True(new FileInfo(f).Length <= limit, f + " is over the limit"));

            // Any one part finds all of them, in numeric order.
            IList<string> found = SparseConverter.FindParts(split.Files[1]);
            Assert.Equal(split.Files, found);

            SparseConverter.ToRaw(found, P("joined.img"), null, CancellationToken.None);
            Assert.Equal(raw, File.ReadAllBytes(P("joined.img")));
        }

        [Fact]
        public void Parts_are_ordered_numerically_not_alphabetically()
        {
            byte[] raw = MixedImage(40, 0);
            File.WriteAllBytes(P("raw.img"), raw);
            SparseConverter.ToSparse(P("raw.img"), P("one.simg"), 4096, null, null, CancellationToken.None);
            foreach (int n in new[] { 0, 1, 2, 9, 10, 11 })
                File.Copy(P("one.simg"), P("super.img_sparsechunk." + n));
            File.WriteAllText(P("super.img_sparsechunk.notes"), "not a part");

            IList<string> parts = SparseConverter.FindParts(P("super.img_sparsechunk.10"));
            Assert.Equal(new[] { 0, 1, 2, 9, 10, 11 }.Select(n => P("super.img_sparsechunk." + n)), parts);
            Assert.Equal(new[] { P("raw.img") }, SparseConverter.FindParts(P("raw.img")));
        }

        [Fact]
        public void Recorded_checksums_are_verified()
        {
            byte[] raw = MixedImage(64, 0, seed: 2);
            File.WriteAllBytes(P("raw.img"), raw);
            SparseConverter.ToSparse(P("raw.img"), P("c.simg"), 4096, null, null, CancellationToken.None);

            // Record the image checksum in the header, as "img2simg -c" style tools do.
            byte[] sparse = File.ReadAllBytes(P("c.simg"));
            BitConverter.GetBytes(Crc32.Compute(raw)).CopyTo(sparse, 24);
            File.WriteAllBytes(P("c.simg"), sparse);
            ExpandResult good = SparseConverter.ToRaw(new[] { P("c.simg") }, P("c.img"), null, CancellationToken.None);
            Assert.Equal(1, good.ChecksumsChecked);

            // Damage one byte of raw data inside the first raw chunk.
            SparseChunk firstRaw = SparseImage.Open(P("c.simg")).Chunks.First(c => c.Type == SparseChunkType.Raw);
            sparse[firstRaw.DataOffset + 10] ^= 0xFF;
            File.WriteAllBytes(P("c.simg"), sparse);
            Assert.Throws<InvalidDataException>(() =>
                SparseConverter.ToRaw(new[] { P("c.simg") }, P("c2.img"), null, CancellationToken.None));
        }

        [Fact]
        public void Damaged_sparse_files_are_reported_clearly()
        {
            byte[] raw = MixedImage(64, 0);
            File.WriteAllBytes(P("raw.img"), raw);
            SparseConverter.ToSparse(P("raw.img"), P("d.simg"), 4096, null, null, CancellationToken.None);
            byte[] sparse = File.ReadAllBytes(P("d.simg"));

            File.WriteAllBytes(P("short.simg"), sparse.Take(sparse.Length - 100).ToArray());
            Assert.Throws<InvalidDataException>(() => SparseImage.Open(P("short.simg")));

            byte[] lying = (byte[])sparse.Clone();
            BitConverter.GetBytes(BitConverter.ToUInt32(lying, 16) + 5).CopyTo(lying, 16); // total blocks
            File.WriteAllBytes(P("lying.simg"), lying);
            Assert.Throws<InvalidDataException>(() => SparseImage.Open(P("lying.simg")));

            byte[] version = (byte[])sparse.Clone();
            version[4] = 2;
            File.WriteAllBytes(P("v2.simg"), version);
            Assert.Throws<InvalidDataException>(() => SparseImage.Open(P("v2.simg")));

            Assert.False(SparseImage.IsSparse(P("raw.img")));
        }

        // ---------------------------------------------------------------- super

        static byte[] Filesystem(int size, uint erofsMagic, int seed)
        {
            byte[] data = new byte[size];
            new Random(seed).NextBytes(data);
            if (erofsMagic != 0)
                BitConverter.GetBytes(erofsMagic).CopyTo(data, 1024);
            else
                BitConverter.GetBytes((ushort)0xEF53).CopyTo(data, 1024 + 56);
            return data;
        }

        string BuildSuper(bool expandedHeader, out Dictionary<string, byte[]> contents)
        {
            contents = new Dictionary<string, byte[]>
            {
                { "system_a", Filesystem(3 << 20, 0xE0F5E1E2, 1) },
                { "vendor_a", Filesystem(1 << 20, 0, 2) },
                { "odm_a", Filesystem(256 << 10, 0xE0F5E1E2, 3) },
            };
            SuperBuilder builder = new SuperBuilder { ExpandedHeader = expandedHeader, HeaderFlags = expandedHeader ? 1u : 0u };
            builder.AddGroup("qti_dynamic_partitions_a", 8UL << 30);
            foreach (string name in new[] { "system", "vendor", "odm" })
            {
                builder.AddPartition(name + "_a", "qti_dynamic_partitions_a", new MemoryStream(contents[name + "_a"]));
                builder.AddPartition(name + "_b", "qti_dynamic_partitions_a", null);
            }
            string path = P(expandedHeader ? "super.img" : "super_v0.img");
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                builder.Write(file, 12 << 20);
            return path;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Super_partitions_are_listed_and_extracted(bool expandedHeader)
        {
            Dictionary<string, byte[]> contents;
            string path = BuildSuper(expandedHeader, out contents);

            using (FileStream file = File.OpenRead(path))
            {
                Assert.True(SuperImage.IsSuper(file));
                SuperImage super = SuperImage.Read(file);
                Assert.Equal(10, super.MajorVersion);
                Assert.Equal(expandedHeader, super.IsVirtualAB);
                Assert.Equal(2u, super.MetadataSlotCount);
                Assert.Equal(new[] { "system_a", "system_b", "vendor_a", "vendor_b", "odm_a", "odm_b" }, super.Partitions.Select(p => p.Name));
                Assert.True(super.Partitions.Single(p => p.Name == "system_b").IsEmpty);
                Assert.Equal("qti_dynamic_partitions_a", super.Partitions[0].Group);
                Assert.Equal("super", super.BlockDevices[0].Name);
                Assert.Equal(12UL << 20, super.BlockDevices[0].Size);

                foreach (SuperPartition partition in super.Partitions.Where(p => !p.IsEmpty))
                {
                    using (MemoryStream output = new MemoryStream())
                    {
                        SuperImage.Extract(file, partition, output, null, CancellationToken.None);
                        Assert.Equal(contents[partition.Name], output.ToArray());
                    }
                }
            }
        }

        [Fact]
        public void A_sparse_super_is_unpacked_without_expanding_it()
        {
            Dictionary<string, byte[]> contents;
            string path = BuildSuper(true, out contents);
            SparseConverter.ToSparse(path, P("super.simg"), 4096, 1_500_000, null, CancellationToken.None);

            using (SparseStream stream = SparseStream.Open(SparseConverter.FindParts(P("super.simg.0"))))
            {
                Assert.True(stream.Parts.Count > 1);
                Assert.Equal(ImageKind.Super, ImageProbe.Kind(stream));
                SuperImage super = SuperImage.Read(stream);
                using (MemoryStream output = new MemoryStream())
                {
                    SuperImage.Extract(stream, super.Partitions.Single(p => p.Name == "vendor_a"), output, null, CancellationToken.None);
                    Assert.Equal(contents["vendor_a"], output.ToArray());
                }
            }

            ImageInfo info = ImageProbe.Identify(P("super.simg.0"));
            Assert.True(info.IsSparse);
            Assert.Equal(ImageKind.Super, info.Kind);
            Assert.Equal(12L << 20, info.ImageLength);
        }

        [Fact]
        public void Damaged_primary_metadata_falls_back_to_the_backup()
        {
            Dictionary<string, byte[]> contents;
            string path = BuildSuper(true, out contents);
            byte[] image = File.ReadAllBytes(path);
            image[4096 + 8192 + 300] ^= 0xFF;   // inside slot 0's primary tables
            image[4096 + 20] ^= 0xFF;           // primary geometry checksum
            File.WriteAllBytes(path, image);

            using (FileStream file = File.OpenRead(path))
            {
                SuperImage super = SuperImage.Read(file);
                Assert.True(super.UsedBackup);
                Assert.Equal(6, super.Partitions.Count);
            }

            // Damage the backup too: now it must fail, not guess.
            long backup = 4096 + 8192 + 65536L * 2;
            image[backup + 300] ^= 0xFF;
            File.WriteAllBytes(path, image);
            using (FileStream file = File.OpenRead(path))
                Assert.Throws<InvalidDataException>(() => SuperImage.Read(file));
        }

        [Fact]
        public void Image_kinds_are_recognised()
        {
            Dictionary<string, byte[]> contents;
            BuildSuper(true, out contents);
            File.WriteAllBytes(P("system.img"), contents["system_a"]);
            File.WriteAllBytes(P("vendor.img"), contents["vendor_a"]);
            byte[] boot = new byte[8192];
            Encoding.ASCII.GetBytes("ANDROID!").CopyTo(boot, 0);
            File.WriteAllBytes(P("boot.img"), boot);
            byte[] vbmeta = new byte[4096];
            Encoding.ASCII.GetBytes("AVB0").CopyTo(vbmeta, 0);
            File.WriteAllBytes(P("vbmeta.img"), vbmeta);

            Assert.Equal(ImageKind.Super, ImageProbe.Identify(P("super.img")).Kind);
            Assert.Equal(ImageKind.Erofs, ImageProbe.Identify(P("system.img")).Kind);
            Assert.Equal(ImageKind.Ext4, ImageProbe.Identify(P("vendor.img")).Kind);
            Assert.Equal(ImageKind.BootImage, ImageProbe.Identify(P("boot.img")).Kind);
            Assert.Equal(ImageKind.Vbmeta, ImageProbe.Identify(P("vbmeta.img")).Kind);

            SparseConverter.ToSparse(P("vendor.img"), P("vendor.simg"), 4096, null, null, CancellationToken.None);
            ImageInfo sparse = ImageProbe.Identify(P("vendor.simg"));
            Assert.True(sparse.IsSparse);
            Assert.Equal(ImageKind.Ext4, sparse.Kind);
        }
    }
}
