using FastbootEnhance.Core.Fastboot;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class FastbootDataTests
    {
        // Shaped like real "fastboot getvar all" output from fastbootd, which goes to stderr.
        const string Fastbootd = @"(bootloader) is-userspace:yes
(bootloader) product:sample_a64
(bootloader) secure:yes
(bootloader) unlocked:yes
(bootloader) current-slot:a
(bootloader) max-download-size:0x10000000
(bootloader) snapshot-update-status:none
(bootloader) partition-size:system_a:0x5DC00000
(bootloader) partition-size:boot_a:0x4000000
(bootloader) partition-size:super:0x240000000
(bootloader) is-logical:system_a:yes
(bootloader) is-logical:boot_a:no
(bootloader) is-logical:super:no
all:
Finished. Total time: 0.050s
";

        [Fact]
        public void Reads_the_common_variables()
        {
            FastbootData data = new FastbootData(Fastbootd);

            Assert.True(data.fastbootd);
            Assert.Equal("sample_a64", data.product);
            Assert.True(data.secure);
            Assert.True(data.unlocked);
            Assert.Equal("a", data.current_slot);
            Assert.Equal(0x10000000, data.max_download_size);
            Assert.Equal("none", data.snapshot_update_status);
        }

        [Fact]
        public void Reads_partition_sizes_and_logical_flags()
        {
            FastbootData data = new FastbootData(Fastbootd);

            Assert.Equal(0x5DC00000, data.partition_size["system_a"]);
            Assert.Equal(0x4000000, data.partition_size["boot_a"]);
            Assert.Equal(0x240000000, data.partition_size["super"]);
            Assert.True(data.partition_is_logical["system_a"]);
            Assert.False(data.partition_is_logical["boot_a"]);
        }

        [Fact]
        public void Handles_bootloader_style_spacing_and_crlf()
        {
            FastbootData data = new FastbootData(
                "(bootloader) partition-size:userdata: 0x1BC4000000\r\n" +
                "(bootloader) is-userspace: no\r\n" +
                "(bootloader) unlocked: no\r\n");

            Assert.Equal(0x1BC4000000, data.partition_size["userdata"]);
            Assert.False(data.fastbootd);
            Assert.False(data.unlocked);
        }

        [Theory]
        [InlineData("(bootloader) product:")]
        [InlineData("(bootloader) partition-size:system_a:")]
        [InlineData("(bootloader) is-logical:system_a")]
        [InlineData("(bootloader)")]
        [InlineData("(bootloader) partition-size:system_a: not-a-number")]
        [InlineData("garbage without a prefix")]
        public void Malformed_lines_are_skipped_instead_of_throwing(string line)
        {
            FastbootData data = new FastbootData(line + "\n(bootloader) product:still_read\n");

            Assert.Equal("still_read", data.product);
        }

        [Fact]
        public void An_unparseable_size_is_kept_as_unknown()
        {
            FastbootData data = new FastbootData("(bootloader) partition-size:odd_a: zzz\n");

            Assert.Equal(-1, data.partition_size["odd_a"]);
        }

        [Fact]
        public void A_repeated_partition_keeps_the_last_value()
        {
            FastbootData data = new FastbootData(
                "(bootloader) partition-size:boot_a:0x1000\n(bootloader) partition-size:boot_a:0x2000\n");

            Assert.Equal(0x2000, data.partition_size["boot_a"]);
        }

        [Fact]
        public void Lock_state_is_unknown_when_the_device_does_not_say()
        {
            FastbootData data = new FastbootData("(bootloader) product:x\n");

            Assert.Null(data.unlocked);
        }

        [Fact]
        public void Empty_output_gives_empty_data()
        {
            FastbootData data = new FastbootData("");

            Assert.Empty(data.partition_size);
            Assert.Null(data.product);
            Assert.Null(new FastbootData(null).product);
        }

        [Theory]
        [InlineData("none", false)]
        [InlineData("snapshotted", true)]
        [InlineData("merging", true)]
        public void Pending_update_follows_the_snapshot_status(string status, bool pending)
        {
            FastbootData data = new FastbootData("(bootloader) snapshot-update-status:" + status + "\n");

            Assert.Equal(pending, data.HasPendingUpdate);
        }

        [Fact]
        public void No_snapshot_status_means_nothing_pending()
        {
            Assert.False(new FastbootData("(bootloader) product:x\n").HasPendingUpdate);
        }

        [Fact]
        public void Leftover_cow_partitions_are_noticed()
        {
            Assert.True(new FastbootData("(bootloader) partition-size:system_a-cow:0x1000\n").HasCowPartitions);
            Assert.False(new FastbootData(Fastbootd).HasCowPartitions);
        }
    }
}
