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

        // The super entries of a real package (Infinix Note 60 Pro, X6878, rawprogram XML as
        // shipped): 17 pieces, the first one being super's 99-sector metadata area. The piece
        // files here are short stand-ins; only the first carries real metadata.
        const string InfinixXml = @"<?xml version=""1.0"" ?>
<data>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename="""" label=""frp"" num_partition_sectors=""128"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" size_in_KB=""512.0"" sparse=""false"" start_byte_hex=""0x1c588000"" start_sector=""116104""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""tranfs_1.img"" label=""tranfs"" num_partition_sectors=""7"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""37896""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""tranfs_2.img"" label=""tranfs"" num_partition_sectors=""3326"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""39500""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_1.img"" label=""super"" num_partition_sectors=""99"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""116232""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_2.img"" label=""super"" num_partition_sectors=""264728"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""116488""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_3.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""381448""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_4.img"" label=""super"" num_partition_sectors=""112801"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""381704""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_5.img"" label=""super"" num_partition_sectors=""155967"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""494600""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_6.img"" label=""super"" num_partition_sectors=""2897"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""650760""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_7.img"" label=""super"" num_partition_sectors=""524101"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""653832""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_8.img"" label=""super"" num_partition_sectors=""161287"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""1178120""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_9.img"" label=""super"" num_partition_sectors=""9928"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""1339656""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_10.img"" label=""super"" num_partition_sectors=""952269"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""1349640""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_11.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""2301960""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_12.img"" label=""super"" num_partition_sectors=""926315"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""2302216""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_13.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3228680""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_14.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3228936""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_15.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3229192""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_16.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3229448""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""super_17.img"" label=""super"" num_partition_sectors=""85"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3229704""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""vbmeta_system.img"" label=""vbmeta_system_a"" num_partition_sectors=""16"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" size_in_KB=""64.0"" sparse=""false"" start_byte_hex=""0x37f608000"" start_sector=""3667464""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""metadata_1.img"" label=""metadata"" num_partition_sectors=""2"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3667496""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""metadata_2.img"" label=""metadata"" num_partition_sectors=""8"" partofsingleimage=""false"" physical_partition_number=""0"" readbackverify=""false"" start_sector=""3668008""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""gpt_main0.bin"" label=""PrimaryGPT"" num_partition_sectors=""6"" partofsingleimage=""true"" physical_partition_number=""0"" readbackverify=""false"" size_in_KB=""24.0"" sparse=""false"" start_byte_hex=""0x0"" start_sector=""0""/>
<program SECTOR_SIZE_IN_BYTES=""4096"" file_sector_offset=""0"" filename=""gpt_backup0.bin"" label=""BackupGPT"" num_partition_sectors=""5"" partofsingleimage=""true"" physical_partition_number=""0"" readbackverify=""false"" size_in_KB=""20.0"" sparse=""false"" start_byte_hex=""(4096*NUM_DISK_SECTORS)-20480."" start_sector=""NUM_DISK_SECTORS-5.""/>
</data>";

        [Fact]
        public void A_real_infinix_package_is_read_as_one_super()
        {
            const long superStart = 116232;
            const long superSectors = 3667464 - superStart;   // up to vbmeta_system_a
            File.WriteAllText(P("rawprogram_unsparse0.xml"), InfinixXml);

            // super_1.img: the metadata a Virtual A/B super of that size starts with.
            SuperBuilder builder = new SuperBuilder { SlotCount = 3, HeaderFlags = 1, ExpandedHeader = true };
            builder.AddGroup("main_a", (ulong)(superSectors * 4096 - (1 << 20)));
            builder.AddGroup("main_b", (ulong)(superSectors * 4096 - (1 << 20)));
            builder.AddPartition("system_a", "main_a", null);
            builder.AddPartition("system_b", "main_b", null);
            byte[] metadata = builder.Plan(superSectors * 4096).Metadata;
            Assert.Equal(99 * 4096, metadata.Length);
            File.WriteAllBytes(P("super_1.img"), metadata);
            for (int i = 2; i <= 17; i++)
                File.WriteAllBytes(P("super_" + i + ".img"), new byte[4096]);
            foreach (string other in new[] { "tranfs_1.img", "tranfs_2.img", "metadata_1.img", "metadata_2.img", "vbmeta_system.img" })
                File.WriteAllBytes(P(other), new byte[4096]);

            RawProgramImage pieces = RawProgramImage.TryFind(P("super_10.img"));
            Assert.NotNull(pieces);
            Assert.Equal("super", pieces.Label);
            Assert.Equal(17, pieces.Pieces.Count);
            Assert.Equal(Enumerable.Range(1, 17).Select(i => "super_" + i + ".img"), pieces.Pieces.Select(p => Path.GetFileName(p.Path)));
            Assert.Equal(0, pieces.Pieces[0].Offset);
            Assert.Equal((116488 - superStart) * 4096, pieces.Pieces[1].Offset);
            Assert.Equal((3229704 - superStart) * 4096, pieces.Pieces[16].Offset);
            Assert.Equal(superSectors * 4096, pieces.Length);

            using (Stream stream = pieces.Open())
            {
                Assert.Equal(ImageKind.Super, ImageProbe.Kind(stream));
                SuperImage super = SuperImage.Read(stream);
                Assert.True(super.IsVirtualAB);
                Assert.Equal(3u, super.MetadataSlotCount);
                Assert.Equal((ulong)(superSectors * 4096), super.BlockDevices[0].Size);
            }

            // The other labels cut the same way are found as their own images.
            Assert.Equal("tranfs", RawProgramImage.TryFind(P("tranfs_2.img")).Label);
            Assert.Equal("metadata", RawProgramImage.TryFind(P("metadata_1.img")).Label);
            Assert.Null(RawProgramImage.TryFind(P("vbmeta_system.img")));
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
