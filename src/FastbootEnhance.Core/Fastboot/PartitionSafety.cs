using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>
    /// Partitions where a wrong image (or an erase) can leave a phone that no longer reaches
    /// the bootloader, or loses its IMEI, calibration or keys for good. Writing them asks for
    /// the partition name to be typed.
    /// </summary>
    public static class PartitionSafety
    {
        static readonly HashSet<string> BrickRisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Qualcomm boot chain and firmware
            "xbl", "xbl_config", "xbl_ramdump", "abl", "tz", "tz_mem", "aop", "aop_config", "hyp", "devcfg",
            "keymaster", "cmnlib", "cmnlib64", "qupfw", "uefi", "uefisecapp", "imagefv", "shrm", "cpucp",
            "rpm", "sbl1", "pmic", "storsec", "multiimgoem", "multiimgqti", "featenabler", "apdp", "msadp",
            "secdata", "ddr", "limits", "toolsfv", "qweslicstore", "spunvm",
            // Radio and device identity
            "modem", "modemst1", "modemst2", "fsg", "fsc", "persist", "efs", "sec", "frp", "devinfo",
            "ssd", "keystore", "prov", "dip", "mdtp", "mdtpsecapp", "dsp",
            // MediaTek boot chain and identity
            "preloader", "lk", "tee", "tee1", "tee2", "sspm", "spmfw", "scp", "mcupm", "dpm", "gz", "gz1", "gz2",
            "pi_img", "md1img", "md1dsp", "nvram", "nvdata", "nvcfg", "protect1", "protect2", "proinfo",
            "seccfg", "efuse", "sec1", "otp", "persistent",
        };

        /// <summary>The partition name without its slot suffix: "abl_b" gives "abl".</summary>
        public static string BaseName(string partition)
        {
            if (partition == null)
                return "";
            if (partition.Length > 2 && (partition.EndsWith("_a", StringComparison.Ordinal) || partition.EndsWith("_b", StringComparison.Ordinal)))
                return partition.Substring(0, partition.Length - 2);
            return partition;
        }

        public static bool IsBrickRisk(string partition)
        {
            return BrickRisk.Contains(BaseName(partition));
        }
    }
}
