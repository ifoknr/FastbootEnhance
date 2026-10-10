using System;
using FastbootEnhance.Core.Adb;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class RootHelperTests
    {
        [Theory]
        [InlineData(RootTool.Magisk, true, "init_boot")]
        [InlineData(RootTool.Magisk, false, "boot")]
        [InlineData(RootTool.KernelSU, true, "init_boot")]
        [InlineData(RootTool.APatch, true, "boot")]
        [InlineData(RootTool.APatch, false, "boot")]
        public void The_right_partition_is_patched(RootTool tool, bool hasInitBoot, string partition)
        {
            Assert.Equal(partition, RootHelper.PartitionFor(tool, hasInitBoot));
        }

        [Fact]
        public void The_newest_patched_image_after_sending_is_picked()
        {
            DateTime sent = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
            string listing =
                Unix(sent.AddDays(-3)) + "|8388608|/sdcard/Download/magisk_patched-27000_old.img\n" +
                Unix(sent.AddMinutes(2)) + "|8388608|/sdcard/Download/magisk_patched-28100_AbCdE.img\n" +
                Unix(sent.AddMinutes(1)) + "|8388608|/sdcard/Download/kernelsu_patched_20261010.img\n" +
                Unix(sent.AddMinutes(5)) + "|8388608|/sdcard/Download/fbstudio_init_boot.img\n" +
                Unix(sent.AddMinutes(6)) + "|0|/sdcard/Download/apatch_patched_empty.img\n" +
                "garbage line\n";
            PatchedImage found = RootHelper.FindNewest(listing, sent.AddSeconds(-30));
            Assert.Equal("magisk_patched-28100_AbCdE.img", found.Name);
            Assert.Equal("/sdcard/Download/magisk_patched-28100_AbCdE.img", found.Path);
            Assert.Null(RootHelper.FindNewest(listing, sent.AddMinutes(10)));
            Assert.Null(RootHelper.FindNewest("", sent));
        }

        [Fact]
        public void The_stock_image_has_its_own_name_on_the_phone()
        {
            Assert.Equal("/sdcard/Download/fbstudio_init_boot.img", RootHelper.PhonePath("init_boot"));
        }
    }
}
