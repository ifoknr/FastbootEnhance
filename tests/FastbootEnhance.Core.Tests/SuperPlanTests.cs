using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using FastbootEnhance.Core.Images;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public sealed class SuperPlanTests : IDisposable
    {
        const long Size = 64L << 20;

        readonly string dir = Path.Combine(Path.GetTempPath(), "fbe-super-" + Guid.NewGuid().ToString("N"));

        public SuperPlanTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        string P(string name) => Path.Combine(dir, name);

        /// <summary>Random blocks, zero blocks and a fill pattern, deterministic per seed.</summary>
        static byte[] Image(int blocks, int seed)
        {
            Random random = new Random(seed);
            byte[] data = new byte[blocks * 4096];
            for (int i = 0; i < blocks; i++)
            {
                Span<byte> block = new Span<byte>(data, i * 4096, 4096);
                switch (i % 7)
                {
                    case 0: case 1: case 2: random.NextBytes(block); break;
                    case 3: case 4: break;
                    default:
                        for (int k = 0; k < 4096; k += 4) BitConverter.TryWriteBytes(block.Slice(k, 4), 0xA5A55A5Au);
                        break;
                }
            }
            return data;
        }

        /// <summary>system and vendor raw, odm raw, product as a sparse image.</summary>
        Dictionary<string, string> Fixtures()
        {
            Dictionary<string, string> files = new Dictionary<string, string>
            {
                { "system", P("system.img") },
                { "vendor", P("vendor.img") },
                { "odm", P("odm.img") },
                { "product", P("product.simg") },
            };
            File.WriteAllBytes(files["system"], Image(700, 1));
            File.WriteAllBytes(files["vendor"], Image(300, 2));
            File.WriteAllBytes(files["odm"], Image(64, 3));
            File.WriteAllBytes(P("product.raw"), Image(200, 4));
            SparseConverter.ToSparse(P("product.raw"), files["product"], 4096, null, null, CancellationToken.None);
            return files;
        }

        SuperPlan Plan(SuperSlotMode mode, Dictionary<string, string> files)
        {
            SuperPlan plan = SuperPlan.Create(mode, Size);
            foreach (string name in new[] { "system", "vendor", "odm", "product" })
                plan.AddImage(files[name], name);
            return plan;
        }

        static string Sha256(string path)
        {
            using (FileStream file = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        // SHA-256 of the raw image AOSP's liblp (MetadataBuilder + WriteToImageFile, as lpmake
        // calls them) writes for the same inputs and settings: 64 MiB, 64 KiB metadata, 1 MiB
        // alignment; groups main_a/main_b (or main) sized as AutoGroupSize gives.
        [Theory]
        [InlineData(SuperSlotMode.VirtualAB, "260067f9935aeb5a34f4d27cced683a70a7174c15fbca75db39ebeacb3a1ac4f")]
        [InlineData(SuperSlotMode.AB, "acf1cdcace440fc57ccda9811a5e36577e1cf221e5c6135cff831a5c79959e58")]
        [InlineData(SuperSlotMode.Single, "dcf808916d8ef29692a52c85807bdc14d4fc9e0a74e93a484823dd15084ee9dd")]
        public void Raw_build_is_identical_to_aosp_liblp(SuperSlotMode mode, string expected)
        {
            Dictionary<string, string> files = Fixtures();
            SuperPlan plan = Plan(mode, files);
            string output = P("super.img");
            plan.Build(output, false, null, CancellationToken.None);

            string dump = Environment.GetEnvironmentVariable("FBE_SUPER_FIXTURES");
            if (!string.IsNullOrEmpty(dump))
            {
                Directory.CreateDirectory(dump);
                foreach (string file in Directory.GetFiles(dir))
                    File.Copy(file, Path.Combine(dump, mode + "-" + Path.GetFileName(file)), true);
            }

            Assert.Equal(expected, Sha256(output));
        }

        [Theory]
        [InlineData(SuperSlotMode.VirtualAB)]
        [InlineData(SuperSlotMode.AB)]
        [InlineData(SuperSlotMode.Single)]
        public void Sparse_build_expands_to_the_raw_build_and_verifies(SuperSlotMode mode)
        {
            Dictionary<string, string> files = Fixtures();
            SuperPlan plan = Plan(mode, files);
            plan.Build(P("super.raw"), false, null, CancellationToken.None);
            plan.Build(P("super.simg"), true, null, CancellationToken.None);

            Assert.True(SparseImage.IsSparse(P("super.simg")));
            Assert.True(new FileInfo(P("super.simg")).Length < Size / 4);
            SparseConverter.ToRaw(new[] { P("super.simg") }, P("expanded.img"), null, CancellationToken.None);
            Assert.Equal(Sha256(P("super.raw")), Sha256(P("expanded.img")));

            foreach (string output in new[] { P("super.raw"), P("super.simg") })
            {
                SuperVerifyResult verify = plan.Verify(output, null, CancellationToken.None);
                Assert.True(verify.Ok, string.Join("; ", verify.Problems));
                Assert.Equal(4, verify.Sha256.Count);
            }
        }

        [Fact]
        public void Layouts_follow_the_aosp_build()
        {
            SuperPlan vab = SuperPlan.Create(SuperSlotMode.VirtualAB, Size, "qti_dynamic_partitions");
            Assert.Equal(3u, vab.MetadataSlots);
            Assert.Equal(1u, vab.HeaderFlags);
            Assert.True(vab.ExpandedHeader);
            Assert.Equal(new[] { "qti_dynamic_partitions_a", "qti_dynamic_partitions_b" }, vab.Groups.Select(g => g.Name));
            Assert.All(vab.Groups, g => Assert.Equal((ulong)(Size - (1 << 20)), g.MaximumSize));

            SuperPlan ab = SuperPlan.Create(SuperSlotMode.AB, Size);
            Assert.Equal(3u, ab.MetadataSlots);
            Assert.Equal(0u, ab.HeaderFlags);
            Assert.False(ab.ExpandedHeader);
            Assert.All(ab.Groups, g => Assert.Equal((ulong)((Size - (1 << 20)) / 2), g.MaximumSize));

            SuperPlan single = SuperPlan.Create(SuperSlotMode.Single, Size);
            Assert.Equal(2u, single.MetadataSlots);
            Assert.Equal(new[] { "main" }, single.Groups.Select(g => g.Name));
        }

        [Fact]
        public void Images_fill_the_right_partitions()
        {
            Dictionary<string, string> files = Fixtures();
            SuperPlan plan = SuperPlan.Create(SuperSlotMode.VirtualAB, Size);
            Assert.Equal("system_a", plan.AddImage(files["system"]).Name);
            Assert.Equal(new[] { "system_a", "system_b" }, plan.Partitions.Select(p => p.Name));
            Assert.Equal("main_b", plan.Partitions[1].Group);
            Assert.False(plan.Partitions[1].HasImage);

            // A file with a slot suffix fills that slot; the same name again replaces the image.
            Assert.Equal("vendor_b", plan.AddImage(files["vendor"], "vendor_b").Name);
            Assert.Equal("system_a", plan.AddImage(files["odm"], "system").Name);
            Assert.Equal(64L * 4096, plan.Partitions[0].ImageLength);

            // The sparse image is measured expanded.
            SuperPlanPartition product = plan.AddImage(files["product"]);
            Assert.Equal("product_a", product.Name);
            Assert.True(product.ImageIsSparse);
            Assert.Equal(200L * 4096, product.ImageLength);

            // Switching layout keeps the images, under their names without suffix.
            plan.Arrange(SuperSlotMode.Single, "main");
            Assert.Equal(new[] { "system", "product" }, plan.Partitions.Select(p => p.Name));
            Assert.All(plan.Partitions, p => Assert.True(p.HasImage));

            plan.Remove(plan.Partitions[0]);
            Assert.Equal(new[] { "product" }, plan.Partitions.Select(p => p.Name));
        }

        [Theory]
        [InlineData("system.img", "system")]
        [InlineData("system_a.img", "system_a")]
        [InlineData("vendor.img_sparsechunk.0", "vendor")]
        [InlineData("super.img.3", "super")]
        [InlineData("odm.raw", "odm")]
        [InlineData("my_product", "my_product")]
        public void Names_come_from_file_names(string file, string name)
        {
            Assert.Equal(name, SuperPlan.NameFromFile(Path.Combine("x", file)));
        }

        [Fact]
        public void Problems_are_reported_before_building()
        {
            Dictionary<string, string> files = Fixtures();

            SuperPlan empty = SuperPlan.Create(SuperSlotMode.VirtualAB, 0);
            Assert.Contains(empty.Check().Problems, p => p.Kind == SuperProblemKind.NoSize);
            Assert.Contains(empty.Check().Problems, p => p.Kind == SuperProblemKind.NoImages);

            SuperPlan odd = Plan(SuperSlotMode.VirtualAB, files);
            odd.DeviceSize = Size + 512;
            Assert.Contains(odd.Check().Problems, p => p.Kind == SuperProblemKind.SizeNotAligned);

            SuperPlan small = Plan(SuperSlotMode.Single, files);
            small.DeviceSize = 4L << 20;
            small.UpdateAutoGroupSize();
            SuperPlanCheck smallCheck = small.Check();
            Assert.Contains(smallCheck.Problems, p => p.Kind == SuperProblemKind.DoesNotFit);
            Assert.Contains(smallCheck.Problems, p => p.Kind == SuperProblemKind.GroupFull && p.Subject == "main");

            SuperPlan bad = SuperPlan.Create(SuperSlotMode.Single, Size);
            bad.AddImage(files["odm"], "has space");
            Assert.Contains(bad.Check().Problems, p => p.Kind == SuperProblemKind.BadName);
            Assert.Throws<InvalidOperationException>(() => bad.Build(P("x.img"), false, null, CancellationToken.None));

            SuperPlan ok = Plan(SuperSlotMode.VirtualAB, files);
            SuperPlanCheck okCheck = ok.Check();
            Assert.True(okCheck.CanBuild, string.Join("; ", okCheck.Problems));
            Assert.Equal((700 + 300 + 64 + 200) * 4096L, okCheck.DataBytes);
            Assert.Equal(1L << 20, okCheck.FirstUsable);
            // Each partition starts on a 1 MiB boundary: 3 + 2 + 1 + 1 MiB after the metadata.
            Assert.Equal(8L << 20, okCheck.UsedEnd);
        }

        [Fact]
        public void An_imported_layout_rebuilds_the_same_image()
        {
            Dictionary<string, string> files = Fixtures();
            SuperPlan original = Plan(SuperSlotMode.VirtualAB, files);
            original.Groups[0].MaximumSize = 40L << 20;
            original.Build(P("original.img"), false, null, CancellationToken.None);

            // Unpack it the way Image Tools does, then import its layout and point at the folder.
            string unpacked = P("original_unpacked");
            Directory.CreateDirectory(unpacked);
            SuperImage read;
            using (FileStream stream = File.OpenRead(P("original.img")))
            {
                read = SuperImage.Read(stream);
                foreach (SuperPartition partition in read.Partitions.Where(p => !p.IsEmpty))
                {
                    using (FileStream output = File.Create(Path.Combine(unpacked, partition.Name + ".img")))
                        SuperImage.Extract(stream, partition, output, null, CancellationToken.None);
                }
            }

            SuperPlan imported = SuperPlan.FromImage(read);
            Assert.Equal(SuperSlotMode.Imported, imported.Mode);
            Assert.Equal(Size, imported.DeviceSize);
            Assert.Equal(1u, imported.HeaderFlags);
            Assert.Equal("main", imported.GroupBase);
            Assert.Equal(40UL << 20, imported.Groups[0].MaximumSize);
            Assert.Equal(8, imported.Partitions.Count);
            Assert.Equal(4, imported.AttachFolder(unpacked));

            imported.Build(P("rebuilt.img"), false, null, CancellationToken.None);
            Assert.Equal(Sha256(P("original.img")), Sha256(P("rebuilt.img")));

            // A new image for one partition: added by name into the imported layout.
            imported.AddImage(files["odm"], "system");
            Assert.Equal("system_a", imported.Partitions[0].Name);
            Assert.True(imported.Check().CanBuild);
        }

        [Fact]
        public void Verify_finds_a_changed_partition()
        {
            Dictionary<string, string> files = Fixtures();
            SuperPlan plan = Plan(SuperSlotMode.VirtualAB, files);
            SuperLayout layout = plan.Build(P("super.img"), false, null, CancellationToken.None);

            SuperPlacement vendor = layout.Placements.Single(p => p.Name == "vendor_a");
            using (FileStream file = new FileStream(P("super.img"), FileMode.Open, FileAccess.ReadWrite))
            {
                file.Position = vendor.Offset + 12345;
                int value = file.ReadByte();
                file.Position = vendor.Offset + 12345;
                file.WriteByte((byte)(value ^ 0xFF));
            }

            SuperVerifyResult verify = plan.Verify(P("super.img"), null, CancellationToken.None);
            Assert.False(verify.Ok);
            Assert.Contains(verify.Problems, p => p.StartsWith("vendor_a differs", StringComparison.Ordinal) && p.EndsWith("12345", StringComparison.Ordinal));
        }

        [Fact]
        public void Images_that_end_mid_block_are_padded_with_zeros()
        {
            byte[] data = Image(10, 9).Take(10 * 4096 - 100).ToArray();
            File.WriteAllBytes(P("short.img"), data);
            SuperPlan plan = SuperPlan.Create(SuperSlotMode.Single, Size);
            plan.AddImage(P("short.img"), "short");
            Assert.Equal(10L * 4096, plan.Partitions[0].Allocated);

            plan.Build(P("super.simg"), true, null, CancellationToken.None);
            Assert.True(plan.Verify(P("super.simg"), null, CancellationToken.None).Ok);
        }
    }
}
