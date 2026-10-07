using System.Text;
using System.Text.RegularExpressions;

namespace FastbootEnhance.FakeAdb
{
    /// <summary>
    /// Pretends to be adb talking to one phone booted into a custom recovery, where adb runs
    /// as root. It answers the commands the Backup page sends: devices, id -u, the partition
    /// listing script, exec-out cat of a partition (deterministic bytes, so hashes can be
    /// checked), stat folder listings and pull. Every call is logged to fake-adb.log.
    /// </summary>
    static class Program
    {
        const string Serial = "FBE0SAMPLE02";
        const long MiB = 1 << 20;

        static readonly (string name, long size)[] Partitions =
        {
            ("boot_a", 16 * MiB), ("boot_b", 16 * MiB),
            ("init_boot_a", 8 * MiB), ("init_boot_b", 8 * MiB),
            ("vendor_boot_a", 16 * MiB), ("vendor_boot_b", 16 * MiB),
            ("dtbo_a", 4 * MiB), ("dtbo_b", 4 * MiB),
            ("vbmeta_a", 64 * 1024), ("vbmeta_b", 64 * 1024),
            ("vbmeta_system_a", 8 * 1024), ("vbmeta_system_b", 8 * 1024),
            ("modem_a", 32 * MiB), ("modem_b", 32 * MiB),
            ("persist", 16 * MiB), ("modemst1", 2 * MiB), ("modemst2", 2 * MiB),
            ("fsg", 2 * MiB), ("fsc", 128 * 1024), ("devinfo", 4096),
            ("misc", MiB), ("metadata", 16 * MiB), ("frp", 512 * 1024),
            ("super", 8L << 30), ("userdata", 110L << 30),
        };

        // A small phone storage: folder path -> (name, size or -1 for a folder).
        static readonly Dictionary<string, (string name, long size)[]> Tree = new Dictionary<string, (string, long)[]>
        {
            ["/sdcard"] = new[]
            {
                ("Alarms", -1L), ("Android", -1L), ("DCIM", -1L), ("Documents", -1L), ("Download", -1L),
                ("Music", -1L), ("Pictures", -1L), (".nomedia", 0L), ("notes.txt", 1234L),
            },
            ["/sdcard/DCIM"] = new[] { ("Camera", -1L), ("Screenshots", -1L) },
            ["/sdcard/DCIM/Camera"] = new[]
            {
                ("IMG_20260901_101500.jpg", 2516582L), ("IMG_20260902_183000.jpg", 3250585L),
                ("VID_20260903_120000.mp4", 48 * MiB),
            },
            ["/sdcard/DCIM/Screenshots"] = new[] { ("Screenshot_20260904.png", 412000L) },
            ["/sdcard/Documents"] = new[] { ("invoice 2026.pdf", 214000L), ("Ahmed's notes.txt", 4096L) },
            ["/sdcard/Download"] = new[] { ("platform-tools.zip", 8 * MiB) },
            ["/sdcard/Pictures"] = new[] { ("wallpaper.png", 1800000L) },
            ["/sdcard/Alarms"] = new (string, long)[0],
            ["/sdcard/Android"] = new (string, long)[0],
            ["/sdcard/Music"] = new (string, long)[0],
        };

        static int Main(string[] args)
        {
            Log(string.Join(" | ", args));

            List<string> rest = new List<string>(args);
            if (rest.Count >= 2 && rest[0] == "-s")
            {
                if (rest[1] != Serial)
                {
                    Console.Error.WriteLine("adb: device '" + rest[1] + "' not found");
                    return 1;
                }
                rest.RemoveRange(0, 2);
            }

            string command = rest.Count > 0 ? rest[0] : "";
            string tail = string.Join(" ", rest.Skip(1));
            switch (command)
            {
                case "devices":
                    Console.Out.Write("List of devices attached\n" + Serial
                        + "\trecovery product:sample_a64 model:Sample_A64 device:sample transport_id:3\n\n");
                    return 0;
                case "kill-server":
                case "start-server":
                    return 0;
                case "shell":
                    return Shell(tail);
                case "exec-out":
                    return ExecOut(tail);
                case "pull":
                    return Pull(rest.Skip(1).Where(a => a != "-a").ToList());
                default:
                    Console.Error.WriteLine("fake adb: unsupported command " + command);
                    return 1;
            }
        }

        static int Shell(string command)
        {
            if (command == "id -u" || command == "su -c 'id -u'")
            {
                Console.Out.Write("0\n");
                return 0;
            }

            if (command.Contains("/dev/block/by-name") && command.Contains("blockdev"))
            {
                StringBuilder listing = new StringBuilder("DIR:/dev/block/by-name\n");
                foreach (var (name, size) in Partitions)
                    listing.Append("P:").Append(name).Append(':').Append(size).Append('\n');
                Console.Out.Write(listing.ToString());
                return 0;
            }

            if (command.StartsWith("stat -c", StringComparison.Ordinal))
            {
                string folder = FirstShellQuoted(command);
                if (folder == null || !Tree.TryGetValue(folder, out var children))
                    return 1;
                StringBuilder listing = new StringBuilder();
                listing.Append("directory|4096|").Append(folder).Append("/.\n");
                listing.Append("directory|4096|").Append(folder).Append("/..\n");
                foreach (var (name, size) in children)
                {
                    listing.Append(size < 0 ? "directory|4096|" : size == 0 ? "regular empty file|0|" : "regular file|" + size + "|")
                        .Append(folder).Append('/').Append(name).Append('\n');
                }
                Console.Out.Write(listing.ToString());
                return 0;
            }

            return 0;
        }

        /// <summary>Undoes AdbCommand.ShellQuote on the first quoted word: 'it'\''s' is it's.</summary>
        static string FirstShellQuoted(string command)
        {
            int start = command.IndexOf('\'');
            if (start < 0)
                return null;
            StringBuilder value = new StringBuilder();
            int i = start + 1;
            while (i < command.Length)
            {
                if (command[i] == '\'')
                {
                    if (string.CompareOrdinal(command, i, "'\\''", 0, 4) == 0)
                    {
                        value.Append('\'');
                        i += 4;
                        continue;
                    }
                    return value.ToString();
                }
                value.Append(command[i]);
                i++;
            }
            return null;
        }

        static int ExecOut(string command)
        {
            Match match = Regex.Match(command, @"cat /dev/block/by-name/([A-Za-z0-9._-]+)");
            if (!match.Success)
                return 1;
            string name = match.Groups[1].Value;
            long size = Partitions.Where(p => p.name == name).Select(p => p.size).FirstOrDefault();
            if (size <= 0)
                return 0;

            // Deterministic content, different per partition, so a mix-up would show in the hashes.
            using (Stream output = Console.OpenStandardOutput())
            {
                byte[] block = new byte[MiB];
                uint state = 2166136261;
                foreach (char c in name)
                    state = (state ^ c) * 16777619;
                long left = size;
                while (left > 0)
                {
                    for (int i = 0; i < block.Length; i += 4)
                    {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        BitConverter.TryWriteBytes(new Span<byte>(block, i, 4), state);
                    }
                    int chunk = (int)Math.Min(block.Length, left);
                    output.Write(block, 0, chunk);
                    left -= chunk;
                }
            }
            return 0;
        }

        static int Pull(List<string> paths)
        {
            if (paths.Count != 2)
            {
                Console.Error.WriteLine("adb: usage: pull [-a] REMOTE LOCAL");
                return 1;
            }
            string remote = paths[0].TrimEnd('/');
            string local = paths[1];
            string parent = remote.Substring(0, Math.Max(1, remote.LastIndexOf('/')));
            string name = remote.Substring(remote.LastIndexOf('/') + 1);

            int files = 0;
            long bytes = 0;
            if (Tree.ContainsKey(remote))
            {
                Copy(remote, Path.Combine(local, name), ref files, ref bytes);
            }
            else if (Tree.TryGetValue(parent, out var siblings) && siblings.Any(s => s.name == name && s.size >= 0))
            {
                long size = siblings.First(s => s.name == name).size;
                Directory.CreateDirectory(local);
                WriteFile(Path.Combine(local, name), size);
                files = 1;
                bytes = size;
            }
            else
            {
                Console.Error.WriteLine("adb: error: failed to stat remote object '" + remote + "': No such file or directory");
                return 1;
            }

            Console.Out.WriteLine(remote + ": " + files + " file" + (files == 1 ? "" : "s") + " pulled, 0 skipped. 40.0 MB/s ("
                + bytes + " bytes in 0.100s)");
            return 0;
        }

        static void Copy(string remote, string local, ref int files, ref long bytes)
        {
            Directory.CreateDirectory(local);
            foreach (var (name, size) in Tree[remote])
            {
                if (size < 0)
                {
                    Copy(remote + "/" + name, Path.Combine(local, name), ref files, ref bytes);
                }
                else
                {
                    WriteFile(Path.Combine(local, name), size);
                    files++;
                    bytes += size;
                }
            }
        }

        static void WriteFile(string path, long size)
        {
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
                file.SetLength(size);
        }

        static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fake-adb.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
