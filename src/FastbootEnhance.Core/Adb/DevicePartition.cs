using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>A partition as listed under /dev/block/by-name.</summary>
    public sealed class DevicePartition
    {
        public DevicePartition(string name, long size)
        {
            Name = name;
            Size = size;
        }

        public string Name { get; }

        /// <summary>Size in bytes, or -1 when the device would not say.</summary>
        public long Size { get; }

        /// <summary>Above this, a backup takes minutes and a lot of disk; the user is asked first.</summary>
        public const long LargeSize = 1L << 30;

        public bool IsLarge => Size > LargeSize;

        // Partitions worth keeping a copy of before flashing anything: what boots the device,
        // what verifies it, and the per-device data that cannot be downloaded again (IMEI and
        // radio calibration, sensor calibration, DRM keys). Matched without the slot suffix.
        static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // boot chain
            "boot", "init_boot", "vendor_boot", "vendor_kernel_boot", "recovery", "dtbo",
            "vbmeta", "vbmeta_system", "vbmeta_vendor",
            // Qualcomm modem and calibration data
            "persist", "persistbak", "modemst1", "modemst2", "fsg", "fsc", "modem", "efs", "sec_efs",
            // MediaTek
            "nvram", "nvdata", "nvcfg", "protect1", "protect2", "proinfo", "md1img", "seccfg",
            // Samsung, Huawei and others
            "efs_backup", "oeminfo", "devinfo",
        };

        /// <summary>True for the partitions the "critical" shortcut selects.</summary>
        public bool IsCritical
        {
            get
            {
                string bare = Name;
                if (bare.EndsWith("_a", StringComparison.Ordinal) || bare.EndsWith("_b", StringComparison.Ordinal))
                    bare = bare.Substring(0, bare.Length - 2);
                return Critical.Contains(bare) && !IsLarge;
            }
        }

        /// <summary>
        /// Parses the output of <see cref="AdbCommand.ListPartitionsScript"/>. Names that
        /// could not be put into a command safely are dropped. Returns the by-name directory
        /// through <paramref name="directory"/>, or null when the device has none.
        /// </summary>
        public static List<DevicePartition> ParseListing(string output, out string directory)
        {
            directory = null;
            List<DevicePartition> partitions = new List<DevicePartition>();
            if (string.IsNullOrEmpty(output))
                return partitions;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("DIR:", StringComparison.Ordinal))
                {
                    string dir = line.Substring(4);
                    if (AdbCommand.IsSafeDevicePath(dir))
                        directory = dir;
                    continue;
                }

                if (!line.StartsWith("P:", StringComparison.Ordinal))
                    continue;

                int colon = line.LastIndexOf(':');
                if (colon <= 2)
                    continue;
                string name = line.Substring(2, colon - 2);
                string sizeText = line.Substring(colon + 1);
                if (!AdbCommand.IsSafePartitionName(name) || !seen.Add(name))
                    continue;

                long size;
                if (!long.TryParse(sizeText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out size) || size <= 0)
                    size = -1;
                partitions.Add(new DevicePartition(name, size));
            }

            partitions.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
            return partitions;
        }
    }
}
