using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastbootEnhance.Core.Fastboot;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class SuperSpaceTests
    {
        const long MiB = 1L << 20;
        const long GiB = 1L << 30;

        // A Virtual A/B phone in fastbootd: 8 GiB super, slot a in use, slot b empty.
        static FastbootData Phone(string extra = "", bool fastbootd = true)
        {
            return new FastbootData(
                "(bootloader) is-userspace:" + (fastbootd ? "yes" : "no") + "\n" +
                "(bootloader) current-slot:a\n" +
                "(bootloader) super-partition-name:super\n" +
                "(bootloader) partition-size:super:0x200000000\n" +
                "(bootloader) is-logical:super:no\n" +
                "(bootloader) partition-size:boot_a:0x4000000\n" +
                "(bootloader) is-logical:boot_a:no\n" +
                "(bootloader) partition-size:system_a:0x80000000\n" +
                "(bootloader) is-logical:system_a:yes\n" +
                "(bootloader) partition-size:vendor_a:0x40000000\n" +
                "(bootloader) is-logical:vendor_a:yes\n" +
                "(bootloader) partition-size:system_b:0x0\n" +
                "(bootloader) is-logical:system_b:yes\n" +
                extra);
        }

        static KeyValuePair<string, long> W(string name, long size)
        {
            return new KeyValuePair<string, long>(name, size);
        }

        [Fact]
        public void Super_is_split_into_slots_cow_and_free_space()
        {
            SuperSpace space = SuperSpace.From(Phone(
                "(bootloader) partition-size:product_b:0x20000000\n(bootloader) is-logical:product_b:yes\n" +
                "(bootloader) partition-size:system_a-cow:0x10000000\n(bootloader) is-logical:system_a-cow:yes\n"));

            Assert.True(space.Known);
            Assert.Equal(8 * GiB, space.SuperSize);
            Assert.Equal(3 * GiB, space.CurrentSlotBytes);
            Assert.Equal(512 * MiB, space.OtherSlotBytes);
            Assert.Equal(256 * MiB, space.CowBytes);
            Assert.Equal(8 * GiB - SuperSpace.MetadataReserve - 3 * GiB - 768 * MiB, space.Free);
            Assert.Equal("product_b", space.OtherSlotPartitions.Single().Name);
            Assert.Equal("system_a-cow", space.CowPartitions.Single().Name);
        }

        [Fact]
        public void Sizes_are_counted_on_1_MiB_boundaries()
        {
            Assert.Equal(0, SuperSpace.Align(0));
            Assert.Equal(MiB, SuperSpace.Align(1));
            Assert.Equal(MiB, SuperSpace.Align(MiB));
            Assert.Equal(2 * MiB, SuperSpace.Align(MiB + 4096));
        }

        [Fact]
        public void Super_size_is_unknown_without_it_and_nothing_is_refused()
        {
            SuperSpace space = SuperSpace.From(new FastbootData(
                "(bootloader) is-userspace:yes\n(bootloader) current-slot:a\n" +
                "(bootloader) partition-size:system_a:0x80000000\n(bootloader) is-logical:system_a:yes\n"));
            Assert.False(space.Known);
            Assert.Equal(-1, space.Free);
            Assert.True(space.Check(new[] { W("system", 100 * GiB) }).Fits);
        }

        [Fact]
        public void A_bigger_image_fits_when_super_has_room()
        {
            SuperSpace space = SuperSpace.From(Phone());
            SuperFit fit = space.Check(new[] { W("system", 3 * GiB) });
            Assert.True(fit.Fits);
            Assert.Equal(GiB, fit.Needed);
            Assert.Equal(new[] { "system_a" }, fit.Growing);
        }

        [Fact]
        public void An_image_too_big_for_super_does_not_fit()
        {
            SuperSpace space = SuperSpace.From(Phone());
            SuperFit fit = space.Check(new[] { W("system_a", 8 * GiB) });
            Assert.False(fit.Fits);
            Assert.Equal(6 * GiB - space.Free, fit.Shortfall);
        }

        [Fact]
        public void The_fullest_point_counts_not_only_the_total()
        {
            // vendor grows by 5 GiB first and only then system shrinks by 2 GiB: at the
            // fullest point 5 GiB more is needed, although the total change is 3 GiB.
            SuperSpace space = SuperSpace.From(Phone());
            SuperFit fit = space.Check(new[] { W("vendor", 6 * GiB), W("system", 0) });
            Assert.False(fit.Fits);
            Assert.Equal(5 * GiB, fit.Needed);

            SuperFit shrinkFirst = space.Check(new[] { W("system", 0), W("vendor", 6 * GiB) });
            Assert.True(shrinkFirst.Fits);
            Assert.Equal(3 * GiB, shrinkFirst.Needed);
        }

        [Fact]
        public void Partitions_outside_super_and_unknown_names_are_ignored_unless_created()
        {
            SuperSpace space = SuperSpace.From(Phone());
            Assert.Equal(0, space.Check(new[] { W("boot", 64 * MiB), W("odm_dlkm", 64 * MiB) }).Needed);
            Assert.Equal(64 * MiB, space.Check(new[] { W("odm_dlkm_a", 64 * MiB) }, new[] { "odm_dlkm_a" }).Needed);
        }

        [Fact]
        public void A_sparse_image_counts_at_its_written_size()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fbe-written-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string sparse = Path.Combine(dir, "big.img");
                const uint blocks = (uint)((12L << 30) / 4096);
                using (BinaryWriter w = new BinaryWriter(File.Create(sparse)))
                {
                    w.Write(0xED26FF3Au); w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)28); w.Write((ushort)12);
                    w.Write(4096u); w.Write(blocks); w.Write(1u); w.Write(0u);
                    w.Write((ushort)0xCAC3); w.Write((ushort)0); w.Write(blocks); w.Write(12u);
                }
                Assert.Equal(12 * GiB, SuperSpace.WrittenSize(sparse));

                string raw = Path.Combine(dir, "raw.img");
                File.WriteAllBytes(raw, new byte[12345]);
                Assert.Equal(12345, SuperSpace.WrittenSize(raw));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Missing_logical_partitions_can_be_created_only_in_fastbootd()
        {
            string[] written = { "boot", "system", "odm_dlkm", "modem" };
            HashSet<string> logical = new HashSet<string> { "system", "odm_dlkm" };

            MissingPartitions inFastbootd = MissingPartitions.Find(Phone(), written, logical);
            Assert.Equal(new[] { "odm_dlkm_a" }, inFastbootd.Create);
            Assert.Equal(new[] { "odm_dlkm" }, inFastbootd.CreateFrom);
            Assert.Equal(new[] { "modem" }, inFastbootd.Unknown);

            MissingPartitions inBootloader = MissingPartitions.Find(Phone(fastbootd: false), written, logical);
            Assert.Empty(inBootloader.Create);
            Assert.Equal(new[] { "odm_dlkm", "modem" }, inBootloader.Unknown);

            Assert.True(MissingPartitions.Find(Phone(), new[] { "boot", "system" }, logical).None);
        }
    }
}
