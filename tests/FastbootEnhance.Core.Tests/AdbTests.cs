using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FastbootEnhance.Core.Adb;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class AdbTests
    {
        [Fact]
        public void Parses_devices_in_every_state()
        {
            const string output = "* daemon not running; starting now at tcp:5037\n" +
                                  "* daemon started successfully\n" +
                                  "List of devices attached\n" +
                                  "R58M12ABCDE            device usb:1-1 product:beyond1ltexx model:SM_G973F device:beyond1 transport_id:2\r\n" +
                                  "FBE0SAMPLE02\trecovery product:sample_a64 model:Sample_A64 device:sample transport_id:3\n" +
                                  "0123456789ABCDEF       unauthorized usb:1-2 transport_id:4\n" +
                                  "192.168.1.20:5555      offline\n" +
                                  "4f5e6d7c               no permissions (user in plugdev group); see [http://developer.android.com/tools/device.html] usb:3-1\n\n";

            List<AdbDevice> devices = AdbDevice.ParseList(output);

            Assert.Equal(5, devices.Count);
            Assert.Equal("R58M12ABCDE", devices[0].Serial);
            Assert.Equal("device", devices[0].State);
            Assert.Equal("SM G973F", devices[0].Model);
            Assert.True(devices[0].Usable);
            Assert.Equal("recovery", devices[1].State);
            Assert.True(devices[1].Usable);
            Assert.True(devices[2].Unauthorized);
            Assert.False(devices[2].Usable);
            Assert.Equal("192.168.1.20:5555", devices[3].Serial);
            Assert.Equal("no permissions", devices[4].State);
        }

        [Fact]
        public void An_empty_list_has_no_devices()
        {
            Assert.Empty(AdbDevice.ParseList("List of devices attached\n\n"));
            Assert.Empty(AdbDevice.ParseList(null));
        }

        [Fact]
        public void Parses_the_partition_listing()
        {
            const string output = "DIR:/dev/block/by-name\n" +
                                  "P:boot_a:67108864\n" +
                                  "P:boot_b:67108864\n" +
                                  "P:persist:33554432\n" +
                                  "P:userdata:118111600640\n" +
                                  "P:mystery:\n" +
                                  "P:bad name;rm -rf:4096\n" +
                                  "P:boot_a:67108864\n";

            string directory;
            List<DevicePartition> partitions = DevicePartition.ParseListing(output, out directory);

            Assert.Equal("/dev/block/by-name", directory);
            Assert.Equal(new[] { "boot_a", "boot_b", "mystery", "persist", "userdata" }, partitions.Select(p => p.Name));
            Assert.Equal(67108864, partitions[0].Size);
            Assert.Equal(-1, partitions.Single(p => p.Name == "mystery").Size);
            Assert.True(partitions.Single(p => p.Name == "userdata").IsLarge);
        }

        [Fact]
        public void A_listing_without_by_name_has_no_directory()
        {
            string directory;
            Assert.Empty(DevicePartition.ParseListing("", out directory));
            Assert.Null(directory);

            DevicePartition.ParseListing("DIR:/dev/block/x y\nP:boot:1\n", out directory);
            Assert.Null(directory);
        }

        [Theory]
        [InlineData("boot_a", 64L << 20, true)]
        [InlineData("vbmeta_system_b", 4096L, true)]
        [InlineData("persist", 32L << 20, true)]
        [InlineData("modemst1", 2L << 20, true)]
        [InlineData("nvdata", 64L << 20, true)]
        [InlineData("system_a", 3L << 30, false)]
        [InlineData("userdata", 100L << 30, false)]
        [InlineData("super", 8L << 30, false)]
        [InlineData("modem_a", 2L << 30, false)] // critical by name, but too large to pick by default
        public void Critical_selection(string name, long size, bool critical)
        {
            Assert.Equal(critical, new DevicePartition(name, size).IsCritical);
        }

        [Fact]
        public void Parses_a_folder_listing()
        {
            const string output = "directory|4096|/sdcard/.\n" +
                                  "directory|4096|/sdcard/..\n" +
                                  "regular file|1048576|/sdcard/notes.txt\n" +
                                  "directory|4096|/sdcard/DCIM\n" +
                                  "regular empty file|0|/sdcard/.nomedia\n" +
                                  "symbolic link|21|/sdcard/link\n" +
                                  "regular file|12|/sdcard/a|b.txt\n" +
                                  "directory|4096|/sdcard/Android\n";

            List<DeviceEntry> entries = DeviceEntry.ParseListing(output);

            Assert.Equal(new[] { "Android", "DCIM", ".nomedia", "a|b.txt", "link", "notes.txt" }, entries.Select(e => e.Name));
            Assert.True(entries[0].IsFolder);
            Assert.Equal(-1, entries[0].Size);
            Assert.Equal(1048576, entries.Single(e => e.Name == "notes.txt").Size);
            Assert.Equal("/sdcard/a|b.txt", entries.Single(e => e.Name == "a|b.txt").Path);
            Assert.True(entries.Single(e => e.Name == "link").IsLink);
        }

        [Theory]
        [InlineData("/sdcard/DCIM", "/sdcard")]
        [InlineData("/sdcard/DCIM/", "/sdcard")]
        [InlineData("/sdcard", "/")]
        [InlineData("/", "/")]
        public void Parent_folder(string path, string parent)
        {
            Assert.Equal(parent, DeviceEntry.Parent(path));
        }

        [Fact]
        public void Partition_names_and_paths_are_checked_before_use()
        {
            Assert.True(AdbCommand.IsSafePartitionName("vbmeta_system_a"));
            Assert.False(AdbCommand.IsSafePartitionName("boot;reboot"));
            Assert.False(AdbCommand.IsSafePartitionName(".."));
            Assert.False(AdbCommand.IsSafePartitionName("a b"));
            Assert.True(AdbCommand.IsSafeDevicePath("/dev/block/platform/soc/1d84000.ufshc/by-name"));
            Assert.False(AdbCommand.IsSafeDevicePath("/dev/block/../../data"));
            Assert.False(AdbCommand.IsSafeDevicePath("dev/block"));

            Assert.Equal("cat /dev/block/by-name/boot_a 2>/dev/null",
                AdbCommand.ReadPartitionCommand("/dev/block/by-name", "boot_a"));
            Assert.Throws<ArgumentException>(() => AdbCommand.ReadPartitionCommand("/dev/block/by-name", "boot_a$(reboot)"));
        }

        [Fact]
        public void Su_wraps_once_and_refuses_single_quotes()
        {
            Assert.Equal("su -c 'id -u'", AdbCommand.AsRoot("id -u", RootAccess.Su));
            Assert.Equal("id -u", AdbCommand.AsRoot("id -u", RootAccess.Direct));
            Assert.Throws<ArgumentException>(() => AdbCommand.AsRoot("echo 'x'", RootAccess.Su));
            Assert.DoesNotContain("'", AdbCommand.ListPartitionsScript);
        }

        // The rules of CommandLineToArgvW, which adb.exe's C runtime follows.
        static List<string> SplitWindowsCommandLine(string line)
        {
            List<string> args = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false, any = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\')
                {
                    int count = 0;
                    while (i < line.Length && line[i] == '\\') { count++; i++; }
                    if (i < line.Length && line[i] == '"')
                    {
                        current.Append('\\', count / 2);
                        if (count % 2 == 1) current.Append('"');
                        else { inQuotes = !inQuotes; }
                        any = true;
                    }
                    else
                    {
                        current.Append('\\', count);
                        i--;
                    }
                    any = true;
                    continue;
                }
                if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
                if ((c == ' ' || c == '\t') && !inQuotes)
                {
                    if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                    continue;
                }
                current.Append(c);
                any = true;
            }
            if (any) args.Add(current.ToString());
            return args;
        }

        [Theory]
        [InlineData("plain")]
        [InlineData("with space")]
        [InlineData("quote \" inside")]
        [InlineData("trailing backslash\\")]
        [InlineData("C:\\Users\\me\\My Backups\\")]
        [InlineData("back\\\\slashes\\\" and quote")]
        [InlineData("")]
        [InlineData("su -c 'for d in /dev/block/by-name; do echo \"$d\"; done'")]
        public void Windows_arguments_survive_the_command_line(string value)
        {
            string line = "adb " + AdbCommand.WindowsArgument(value) + " next";
            List<string> args = SplitWindowsCommandLine(line);
            Assert.Equal(new[] { "adb", value, "next" }, args);
        }

        [Fact]
        public void Pull_and_shell_put_each_argument_in_its_own_slot()
        {
            List<string> pull = SplitWindowsCommandLine(
                AdbCommand.Pull("FBE0SAMPLE02", "/sdcard/My \"Docs\"", "C:\\Back ups\\"));
            Assert.Equal(new[] { "-s", "FBE0SAMPLE02", "pull", "-a", "/sdcard/My \"Docs\"", "C:\\Back ups\\" }, pull);

            List<string> shell = SplitWindowsCommandLine(AdbCommand.Shell("S1", "stat -c %F -- '/sdcard/a b'/*"));
            Assert.Equal(new[] { "-s", "S1", "shell", "stat -c %F -- '/sdcard/a b'/*" }, shell);
        }

        static string RunSh(string script)
        {
            ProcessStartInfo start = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            using (Process sh = Process.Start(start))
            {
                string output = sh.StandardOutput.ReadToEnd();
                sh.WaitForExit();
                Assert.True(sh.ExitCode == 0, sh.StandardError.ReadToEnd());
                return output;
            }
        }

        [Theory]
        [InlineData("/sdcard/it's here")]
        [InlineData("/sdcard/$HOME `id` \"x\" \\ end")]
        [InlineData("/sdcard/a  b")]
        public void Shell_quoting_reaches_the_shell_unchanged(string value)
        {
            if (!File.Exists("/bin/sh"))
                return;
            Assert.Equal(value, RunSh("printf %s " + AdbCommand.ShellQuote(value)));
        }

        [Fact]
        public void The_partition_script_is_valid_shell()
        {
            if (!File.Exists("/bin/sh"))
                return;
            RunSh("sh -n -c " + AdbCommand.ShellQuote(AdbCommand.ListPartitionsScript));
        }

        [Fact]
        public void The_folder_listing_command_runs_and_parses()
        {
            if (!File.Exists("/usr/bin/stat") && !File.Exists("/bin/stat"))
                return;

            string folder = Path.Combine(Path.GetTempPath(), "fbe adb 'test' " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(folder, "Sub Folder"));
            File.WriteAllText(Path.Combine(folder, "a file.txt"), "hello");
            File.WriteAllText(Path.Combine(folder, ".hidden"), "x");
            try
            {
                List<DeviceEntry> entries = DeviceEntry.ParseListing(RunSh(AdbCommand.ListFolderCommand(folder)));
                Assert.Equal(new[] { "Sub Folder", ".hidden", "a file.txt" }, entries.Select(e => e.Name));
                Assert.Equal(5, entries.Single(e => e.Name == "a file.txt").Size);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void Checksums_round_trip_and_catch_damage()
        {
            string folder = Path.Combine(Path.GetTempPath(), "fbe-sums-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllBytes(Path.Combine(folder, "boot_a.img"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(folder, "persist.img"), new byte[] { 4, 5, 6 });
                Dictionary<string, string> hashes = new Dictionary<string, string>
                {
                    { "boot_a.img", Sha256Sums.HashFile(Path.Combine(folder, "boot_a.img")) },
                    { "persist.img", Sha256Sums.HashFile(Path.Combine(folder, "persist.img")) },
                };
                File.WriteAllText(Path.Combine(folder, Sha256Sums.FileName), Sha256Sums.Format(hashes));

                VerifyReport clean = Sha256Sums.Verify(folder);
                Assert.True(clean.AllGood);
                Assert.Equal(2, clean.Total);

                File.WriteAllBytes(Path.Combine(folder, "persist.img"), new byte[] { 4, 5, 7 });
                File.AppendAllText(Path.Combine(folder, Sha256Sums.FileName),
                    new string('0', 64) + "  ../escape.img\n" + new string('0', 64) + " *gone.img\n");

                VerifyReport damaged = Sha256Sums.Verify(folder);
                Assert.False(damaged.AllGood);
                Assert.Equal(new[] { "boot_a.img" }, damaged.Good);
                Assert.Equal(3, damaged.Bad.Count);
                Assert.Contains(damaged.Bad, b => b.StartsWith("persist.img"));
                Assert.Contains(damaged.Bad, b => b.StartsWith("../escape.img"));
                Assert.Contains(damaged.Bad, b => b.StartsWith("gone.img"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }

            Assert.False(Sha256Sums.Verify(Path.GetTempPath() + Guid.NewGuid().ToString("N")).HadSums);
        }

        [Fact]
        public void Hashing_copy_matches_a_separate_hash()
        {
            byte[] data = new byte[(5 << 20) + 123];
            new Random(7).NextBytes(data);
            List<long> reports = new List<long>();
            using (MemoryStream source = new MemoryStream(data))
            using (MemoryStream destination = new MemoryStream())
            {
                HashingCopy.Result result = HashingCopy.Copy(source, destination,
                    new SyncProgress(reports.Add), CancellationToken.None);
                Assert.Equal(data.Length, result.Bytes);
                Assert.Equal(data, destination.ToArray());
                using (SHA256 sha = SHA256.Create())
                    Assert.Equal(Sha256Sums.Hex(sha.ComputeHash(data)), result.Sha256);
            }
            Assert.Equal(data.Length, reports.Last());
        }

        sealed class SyncProgress : IProgress<long>
        {
            readonly Action<long> action;
            public SyncProgress(Action<long> action) { this.action = action; }
            public void Report(long value) { action(value); }
        }
    }
}
