namespace FastbootEnhance.FakeFastboot
{
    /// <summary>
    /// Pretends to be fastboot talking to one A/B device sitting in fastbootd. It answers the
    /// commands the app sends with output shaped like the real tool's (status on stderr,
    /// "devices" on stdout) so the UI can be driven end to end in CI. Every command is
    /// appended to fake-fastboot.log next to the executable, so a run can be checked.
    /// "reboot bootloader" moves it to the bootloader (remembered in fake-fastboot.mode), where
    /// "boot" works; any other reboot puts it back in fastbootd.
    /// </summary>
    static class Program
    {
        const string Serial = "FBE0SAMPLE01";

        // Partitions the sample OTA contains, with sizes large enough to take its images.
        static readonly (string name, long size, bool logical)[] Partitions =
        {
            ("boot_a", 0x4000000, false), ("boot_b", 0x4000000, false),
            ("init_boot_a", 0x800000, false), ("init_boot_b", 0x800000, false),
            ("vendor_boot_a", 0x4000000, false), ("vendor_boot_b", 0x4000000, false),
            ("dtbo_a", 0x800000, false), ("dtbo_b", 0x800000, false),
            ("vbmeta_a", 0x10000, false), ("vbmeta_b", 0x10000, false),
            ("vbmeta_system_a", 0x10000, false), ("vbmeta_system_b", 0x10000, false),
            ("abl_a", 0x800000, false), ("abl_b", 0x800000, false),
            ("xbl_a", 0x600000, false), ("xbl_b", 0x600000, false),
            ("system_a", 0x10000000, true), ("system_b", 0, true),
            ("system_ext_a", 0x6000000, true), ("system_ext_b", 0, true),
            ("product_a", 0x8000000, true), ("product_b", 0, true),
            ("vendor_a", 0x6000000, true), ("vendor_b", 0, true),
            ("vendor_dlkm_a", 0x2000000, true), ("vendor_dlkm_b", 0, true),
            ("odm_a", 0x800000, true), ("odm_b", 0, true),
            ("super", 0x240000000, false),
            ("metadata", 0x1000000, false),
            ("misc", 0x100000, false),
            ("userdata", 0x1BC4000000, false),
        };

        static int Main(string[] args)
        {
            List<string> rest = new List<string>(args);
            if (rest.Count >= 2 && rest[0] == "-s")
                rest.RemoveRange(0, 2);

            Log(string.Join(" ", args));

            // Options such as --disable-verity come before the command, as fastboot takes them.
            while (rest.Count > 0 && rest[0].StartsWith("--", StringComparison.Ordinal))
                rest.RemoveAt(0);

            string command = rest.Count > 0 ? rest[0] : "";
            switch (command)
            {
                case "devices":
                    Console.Out.WriteLine(Serial + "\tfastboot");
                    return 0;

                case "getvar":
                    GetVarAll();
                    return 0;

                case "flash":
                    return Flash(rest);

                case "boot":
                    return Boot(rest);

                case "reboot":
                    SetBootloader(rest.Count > 1 && rest[1] == "bootloader");
                    Console.Error.WriteLine(Describe(rest) + " OKAY [  0.050s]");
                    Console.Error.WriteLine("Finished. Total time: 0.050s");
                    return 0;

                case "set_active":
                case "erase":
                case "snapshot-update":
                case "delete-logical-partition":
                case "create-logical-partition":
                case "resize-logical-partition":
                    Console.Error.WriteLine(Describe(rest) + " OKAY [  0.050s]");
                    Console.Error.WriteLine("Finished. Total time: 0.050s");
                    return 0;

                default:
                    Console.Error.WriteLine("fastboot: usage: unknown command " + command);
                    return 1;
            }
        }

        static void GetVarAll()
        {
            TextWriter err = Console.Error;
            err.WriteLine("(bootloader) is-userspace:" + (InBootloader() ? "no" : "yes"));
            err.WriteLine("(bootloader) product:sample_a64");
            err.WriteLine("(bootloader) secure:yes");
            err.WriteLine("(bootloader) unlocked:yes");
            err.WriteLine("(bootloader) current-slot:a");
            err.WriteLine("(bootloader) slot-successful:a:yes");
            err.WriteLine("(bootloader) slot-successful:b:yes");
            err.WriteLine("(bootloader) slot-unbootable:a:no");
            err.WriteLine("(bootloader) slot-unbootable:b:no");
            err.WriteLine("(bootloader) slot-retry-count:a:7");
            err.WriteLine("(bootloader) slot-retry-count:b:7");
            err.WriteLine("(bootloader) slot-count:2");
            err.WriteLine("(bootloader) max-download-size:0x10000000");
            err.WriteLine("(bootloader) snapshot-update-status:none");
            err.WriteLine("(bootloader) super-partition-name:super");
            foreach (var p in Partitions)
            {
                err.WriteLine("(bootloader) partition-size:" + p.name + ":0x" + p.size.ToString("X"));
                err.WriteLine("(bootloader) is-logical:" + p.name + ":" + (p.logical ? "yes" : "no"));
            }
            err.WriteLine("all:");
            err.WriteLine("Finished. Total time: 0.012s");
        }

        static int Flash(List<string> rest)
        {
            if (rest.Count < 3)
            {
                Console.Error.WriteLine("fastboot: error: flash needs a partition and an image");
                return 1;
            }

            string partition = rest[rest.Count - 2];
            string image = rest[rest.Count - 1];
            if (!File.Exists(image))
            {
                Console.Error.WriteLine("fastboot: error: cannot load '" + image + "'");
                return 1;
            }

            string slotted = partition.EndsWith("_a") || partition.EndsWith("_b") || partition == "super"
                ? partition : partition + "_a";
            long kb = new FileInfo(image).Length / 1024;

            Console.Error.WriteLine("Sending '" + slotted + "' (" + kb + " KB)            OKAY [  0.120s]");
            Thread.Sleep(150);
            Console.Error.WriteLine("Writing '" + slotted + "'                          OKAY [  0.080s]");
            Console.Error.WriteLine("Finished. Total time: 0.210s");
            return 0;
        }

        static string ModePath => Path.Combine(AppContext.BaseDirectory, "fake-fastboot.mode");

        static bool InBootloader()
        {
            try
            {
                return File.Exists(ModePath) && File.ReadAllText(ModePath).Trim() == "bootloader";
            }
            catch (IOException)
            {
                return false;
            }
        }

        static void SetBootloader(bool bootloader)
        {
            if (bootloader)
                File.WriteAllText(ModePath, "bootloader");
            else if (File.Exists(ModePath))
                File.Delete(ModePath);
        }

        /// <summary>"fastboot boot IMAGE": refused by fastbootd, as on a real phone.</summary>
        static int Boot(List<string> rest)
        {
            if (!InBootloader())
            {
                Console.Error.WriteLine("Sending 'boot.img'                                 OKAY [  0.100s]");
                Console.Error.WriteLine("Booting                                            FAILED (remote: 'Unrecognized command boot')");
                Console.Error.WriteLine("fastboot: error: Command failed");
                return 1;
            }
            string image = rest.Count > 1 ? rest[1] : "";
            if (!File.Exists(image))
            {
                Console.Error.WriteLine("fastboot: error: cannot load '" + image + "'");
                return 1;
            }
            long kb = new FileInfo(image).Length / 1024;
            Console.Error.WriteLine("Sending 'boot.img' (" + kb + " KB)                    OKAY [  0.150s]");
            Thread.Sleep(150);
            Console.Error.WriteLine("Booting                                            OKAY [  0.050s]");
            Console.Error.WriteLine("Finished. Total time: 0.210s");
            return 0;
        }

        static string Describe(List<string> rest)
        {
            return string.Join(" ", rest).Replace("\"", "");
        }

        static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fake-fastboot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Several pollers can run at once; losing a log line is fine.
            }
        }
    }
}
