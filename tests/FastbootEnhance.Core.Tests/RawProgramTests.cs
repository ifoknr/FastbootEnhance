using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FastbootEnhance.Core.Images;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public sealed class RawProgramTests : IDisposable
    {
        const int Sector = 4096;
        const long DiskStart = 123456;   // where super starts on the disk, in sectors

        readonly string dir = Path.Combine(Path.GetTempPath(), "fbe-rawprogram-" + Guid.NewGuid().ToString("N"));

        public RawProgramTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        string P(string name) => Path.Combine(dir, name);

        static string Sha256(string path)
        {
            using (FileStream file = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>A Virtual A/B super with three partitions, as a raw file.</summary>
        string BuildSuper()
        {
            Random random = new Random(5);
            SuperPlan plan = SuperPlan.Create(SuperSlotMode.VirtualAB, 32L << 20);
            foreach (var (name, blocks) in new[] { ("system", 900), ("vendor", 300), ("odm", 40) })
            {
                byte[] data = new byte[blocks * 4096];
                random.NextBytes(data);
                Array.Clear(data, 4096 * 10, 4096 * Math.Min(50, blocks - 20));   // zeros inside a partition stay data
                File.WriteAllBytes(P(name + ".src"), data);
                plan.AddImage(P(name + ".src"), name);
            }
            string super = P("super.raw");
            plan.Build(super, false, null, CancellationToken.None);
            return super;
        }

        /// <summary>
        /// Cuts the image like Qualcomm's unsparse step: one file per run of non-zero blocks
        /// longer than a few blocks, listed in a rawprogram XML with absolute sectors.
        /// <paramref name="dropReserved"/> leaves out the zero first block, as some tools do.
        /// </summary>
        void CutIntoPieces(string super, bool dropReserved)
        {
            byte[] image = File.ReadAllBytes(super);
            int blocks = image.Length / 4096;
            bool Zero(int b) => image.Skip(b * 4096).Take(4096).All(x => x == 0);

            List<(int start, int count)> runs = new List<(int, int)>();
            int at = dropReserved ? 1 : 0;
            while (at < blocks)
            {
                if (Zero(at) && !(at == 0 && !dropReserved))
                {
                    at++;
                    continue;
                }
                int start = at;
                int zeros = 0;
                while (at < blocks && zeros < 64)
                {
                    zeros = Zero(at) ? zeros + 1 : 0;
                    at++;
                }
                runs.Add((start, at - start - zeros));
            }

            StringBuilder xml = new StringBuilder("<?xml version=\"1.0\" ?>\n<data>\n");
            xml.Append("  <program SECTOR_SIZE_IN_BYTES=\"4096\" file_sector_offset=\"0\" filename=\"boot.img\" label=\"boot_a\" num_partition_sectors=\"16384\" physical_partition_number=\"0\" start_sector=\"6\" />\n");
            for (int i = 0; i < runs.Count; i++)
            {
                string name = "super_" + (i + 1) + ".img";
                File.WriteAllBytes(P(name), image.Skip(runs[i].start * 4096).Take(runs[i].count * 4096).ToArray());
                xml.Append("  <program SECTOR_SIZE_IN_BYTES=\"4096\" file_sector_offset=\"0\" filename=\"" + name + "\" label=\"super\" num_partition_sectors=\""
                           + runs[i].count + "\" partofsingleimage=\"false\" physical_partition_number=\"0\" readbackverify=\"false\" sparse=\"false\" start_sector=\""
                           + (DiskStart + runs[i].start) + "\" />\n");
            }
            xml.Append("  <program SECTOR_SIZE_IN_BYTES=\"4096\" filename=\"\" label=\"last_parti\" num_partition_sectors=\"0\" start_sector=\"NUM_DISK_SECTORS-5.\" />\n");
            xml.Append("</data>\n");
            File.WriteAllText(P("rawprogram_unsparse0.xml"), xml.ToString());
            File.WriteAllText(P("notes.xml"), "<not-rawprogram>");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Pieces_make_the_original_super(bool dropReserved)
        {
            string super = BuildSuper();
            CutIntoPieces(super, dropReserved);

            RawProgramImage pieces = RawProgramImage.TryFind(P("super_2.img"));
            Assert.NotNull(pieces);
            Assert.Equal("super", pieces.Label);
            Assert.True(pieces.Pieces.Count >= 4);
            Assert.Equal(new FileInfo(super).Length, pieces.Length);

            using (Stream stream = pieces.Open())
            {
                Assert.Equal(ImageKind.Super, ImageProbe.Kind(stream));
                SuperImage read = SuperImage.Read(stream);
                Assert.Equal(new[] { "system_a", "system_b", "vendor_a", "vendor_b", "odm_a", "odm_b" }, read.Partitions.Select(p => p.Name));
            }

            pieces.WriteRaw(P("combined.img"), null, CancellationToken.None);
            Assert.Equal(Sha256(super), Sha256(P("combined.img")));

            pieces.WriteSparse(P("combined.simg"), null, CancellationToken.None);
            SparseConverter.ToRaw(new[] { P("combined.simg") }, P("expanded.img"), null, CancellationToken.None);
            Assert.Equal(Sha256(super), Sha256(P("expanded.img")));
        }

        [Fact]
        public void A_normal_image_is_not_taken_for_pieces()
        {
            string super = BuildSuper();
            CutIntoPieces(super, false);
            Assert.Null(RawProgramImage.TryFind(super));
            File.WriteAllBytes(P("boot.img"), new byte[8192]);
            Assert.Null(RawProgramImage.TryFind(P("boot.img")));
        }

        [Fact]
        public void Numbered_parts_are_ordered_by_number()
        {
            Assert.Equal(new[] { "super_1.img", "super_2.img", "super_10.img" },
                ImageNaming.OrderParts(new[] { "super_10.img", "super_2.img", "super_1.img" }).ToArray());
            Assert.Equal(new[] { "x.img.0", "x.img.1", "x.img.12" },
                ImageNaming.OrderParts(new[] { "x.img.12", "x.img.0", "x.img.1" }).ToArray());
        }
    }
}
