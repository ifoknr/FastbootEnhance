using System;
using System.IO;
using System.Linq;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Images;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class SafetyTests
    {
        static FastbootData Data(string extra, bool fastbootd = true)
        {
            return new FastbootData(
                "(bootloader) is-userspace:" + (fastbootd ? "yes" : "no") + "\n" +
                "(bootloader) current-slot:a\n" +
                "(bootloader) partition-size:system_a:0x10000000\n" +
                "(bootloader) partition-size:vendor_a:0x8000000\n" +
                "(bootloader) partition-size:boot_a:0x4000000\n" +
                "(bootloader) partition-size:boot_b:0x4000000\n" +
                "(bootloader) is-logical:system_a:yes\n" +
                "(bootloader) is-logical:vendor_a:yes\n" +
                "(bootloader) is-logical:boot_a:no\n" +
                "(bootloader) is-logical:boot_b:no\n" +
                extra);
        }

        const string BothSlotsFull =
            "(bootloader) partition-size:system_b:0x10000000\n" +
            "(bootloader) partition-size:vendor_b:0x8000000\n" +
            "(bootloader) is-logical:system_b:yes\n" +
            "(bootloader) is-logical:vendor_b:yes\n";

        [Fact]
        public void Slot_state_is_read_per_slot()
        {
            FastbootData data = Data(
                "(bootloader) slot-successful:a:yes\n(bootloader) slot-successful:b:no\n" +
                "(bootloader) slot-unbootable:b:yes\n(bootloader) slot-retry-count:a:7\n(bootloader) slot-retry-count:b:0\n");
            Assert.True(data.slot_successful["a"]);
            Assert.False(data.slot_successful["b"]);
            Assert.True(data.slot_unbootable["b"]);
            Assert.Equal(7, data.slot_retry_count["a"]);
            Assert.Equal(0, data.slot_retry_count["b"]);
        }

        [Fact]
        public void A_healthy_slot_switches_without_findings()
        {
            FastbootData data = Data(BothSlotsFull +
                "(bootloader) slot-successful:b:yes\n(bootloader) slot-unbootable:b:no\n(bootloader) slot-retry-count:b:7\n");
            SlotSwitchCheck check = SlotSwitchCheck.Evaluate(data, "b");
            Assert.Equal(SlotCheckLevel.Ok, check.Level);
            Assert.Empty(check.Findings);
        }

        [Fact]
        public void A_slot_not_booted_yet_is_a_warning_not_an_error()
        {
            // Right after flashing an update to the other slot, it has not booted yet: normal.
            FastbootData data = Data(BothSlotsFull +
                "(bootloader) slot-successful:b:no\n(bootloader) slot-unbootable:b:no\n(bootloader) slot-retry-count:b:7\n");
            SlotSwitchCheck check = SlotSwitchCheck.Evaluate(data, "b");
            Assert.Equal(SlotCheckLevel.Warn, check.Level);
            Assert.Equal(SlotCheckReason.NeverBooted, check.Findings.Single().Reason);
        }

        [Fact]
        public void An_unbootable_slot_or_one_without_retries_is_dangerous()
        {
            SlotSwitchCheck unbootable = SlotSwitchCheck.Evaluate(Data(BothSlotsFull + "(bootloader) slot-unbootable:b:yes\n"), "b");
            Assert.Equal(SlotCheckLevel.Danger, unbootable.Level);
            Assert.Contains(unbootable.Findings, f => f.Reason == SlotCheckReason.Unbootable);

            SlotSwitchCheck spent = SlotSwitchCheck.Evaluate(Data(BothSlotsFull +
                "(bootloader) slot-successful:b:no\n(bootloader) slot-retry-count:b:0\n"), "b");
            Assert.Equal(SlotCheckLevel.Danger, spent.Level);
            Assert.Contains(spent.Findings, f => f.Reason == SlotCheckReason.NoRetriesLeft);
            Assert.DoesNotContain(spent.Findings, f => f.Reason == SlotCheckReason.NeverBooted);
        }

        [Fact]
        public void Empty_logical_partitions_on_the_target_slot_block_the_switch()
        {
            // As after flashing a factory super: the b partitions exist with size 0.
            FastbootData data = Data(
                "(bootloader) partition-size:system_b:0x0\n(bootloader) is-logical:system_b:yes\n" +
                "(bootloader) slot-successful:b:yes\n");
            SlotSwitchCheck check = SlotSwitchCheck.Evaluate(data, "b");
            Assert.Equal(SlotCheckLevel.Block, check.Level);
            SlotCheckFinding finding = check.Findings.Single(f => f.Reason == SlotCheckReason.EmptyPartitions);
            Assert.Equal(new[] { "system_b", "vendor_b" }, finding.Partitions);
        }

        [Fact]
        public void A_bootloader_that_reports_nothing_does_not_stop_the_switch()
        {
            // Many MediaTek bootloaders have no slot-successful; the bootloader also lists no
            // logical partitions, so those cannot be checked there.
            SlotSwitchCheck check = SlotSwitchCheck.Evaluate(Data("", fastbootd: false), "b");
            Assert.Equal(SlotCheckLevel.Ok, check.Level);
            Assert.Contains(check.Findings, f => f.Reason == SlotCheckReason.StateUnknown);
            Assert.Contains(check.Findings, f => f.Reason == SlotCheckReason.PartitionsNotChecked);
        }

        [Theory]
        [InlineData("abl_b", true)]
        [InlineData("xbl", true)]
        [InlineData("modemst1", true)]
        [InlineData("persist", true)]
        [InlineData("PRELOADER", true)]
        [InlineData("nvdata", true)]
        [InlineData("boot_a", false)]
        [InlineData("system", false)]
        [InlineData("vbmeta_a", false)]
        [InlineData("userdata", false)]
        public void Brick_risk_partitions_are_known(string name, bool risky)
        {
            Assert.Equal(risky, PartitionSafety.IsBrickRisk(name));
        }

        static byte[] BootHeader(string magic, uint kernel, uint word12, uint word16, uint version)
        {
            byte[] image = new byte[4096];
            System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(image, 0);
            BitConverter.GetBytes(kernel).CopyTo(image, 8);
            BitConverter.GetBytes(word12).CopyTo(image, 12);
            BitConverter.GetBytes(word16).CopyTo(image, 16);
            BitConverter.GetBytes(version).CopyTo(image, 40);
            return image;
        }

        [Fact]
        public void Boot_image_headers_say_what_can_be_booted()
        {
            // v4 boot.img with a kernel; v4 init_boot (ramdisk only); v2 recovery; vendor_boot.
            BootImageHeader boot = BootImageHeader.Read(new MemoryStream(BootHeader("ANDROID!", 0x1800000, 0x200000, 0, 4)));
            Assert.Equal(BootImageKind.Bootable, boot.Kind);
            Assert.Equal(4, boot.HeaderVersion);
            Assert.Equal(0x200000, boot.RamdiskSize);

            BootImageHeader initBoot = BootImageHeader.Read(new MemoryStream(BootHeader("ANDROID!", 0, 0x800000, 0, 4)));
            Assert.Equal(BootImageKind.NoKernel, initBoot.Kind);

            BootImageHeader recovery = BootImageHeader.Read(new MemoryStream(BootHeader("ANDROID!", 0x1000000, 0x10008000, 0x3000000, 2)));
            Assert.Equal(BootImageKind.Bootable, recovery.Kind);
            Assert.Equal(0x3000000, recovery.RamdiskSize);

            Assert.Equal(BootImageKind.VendorBoot, BootImageHeader.Read(new MemoryStream(BootHeader("VNDRBOOT", 4, 0, 0, 0))).Kind);
            Assert.Equal(BootImageKind.NotBootImage, BootImageHeader.Read(new MemoryStream(new byte[4096])).Kind);
            Assert.Equal(BootImageKind.NotBootImage, BootImageHeader.Read(new MemoryStream(new byte[10])).Kind);
        }
    }
}
