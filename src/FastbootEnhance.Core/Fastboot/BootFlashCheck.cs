using System;
using System.Collections.Generic;
using FastbootEnhance.Core.Adb;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.Core.Fastboot
{
    public enum FindingLevel
    {
        /// <summary>Good to know; flashing goes ahead.</summary>
        Info,

        /// <summary>Probably a mistake: asks before flashing.</summary>
        Warn,

        /// <summary>Will almost certainly stop the phone from starting: asks to type the partition name.</summary>
        Block,
    }

    public enum FindingCode
    {
        NotBootImage,
        NoKernelForBoot,
        KernelInInitBoot,
        VendorBootMismatch,
        KmiMismatch,
        KernelVersionMismatch,
        OlderPatchLevel,
        KernelSummary,
        RootSummary,
        Stock,

        /// <summary>The image is rooted with another root manager than the phone runs (from the Companion).</summary>
        RootManagerChange,
    }

    public sealed class Finding
    {
        public Finding(FindingLevel level, FindingCode code, params string[] values)
        {
            Level = level;
            Code = code;
            Values = values;
        }

        public FindingLevel Level { get; }
        public FindingCode Code { get; }

        /// <summary>The parts the message is built from, in the order of the message's {0}, {1}, ...</summary>
        public IReadOnlyList<string> Values { get; }
    }

    /// <summary>
    /// Checks a boot-type image against the partition it is about to be flashed to, and
    /// against what the phone reported over adb (its kernel and patch level), before
    /// "fastboot flash" writes it.
    /// </summary>
    public static class BootFlashCheck
    {
        static readonly string[] BootPartitions = { "boot", "init_boot", "vendor_boot", "recovery", "vendor_kernel_boot" };

        /// <summary>The partition name without its slot: "init_boot_a" is "init_boot".</summary>
        public static string BaseName(string partition)
        {
            if (partition == null)
                return null;
            return partition.EndsWith("_a", StringComparison.Ordinal) || partition.EndsWith("_b", StringComparison.Ordinal)
                ? partition.Substring(0, partition.Length - 2)
                : partition;
        }

        /// <summary>True for the partitions this check looks at.</summary>
        public static bool Applies(string partition)
        {
            return Array.IndexOf(BootPartitions, BaseName(partition)) >= 0;
        }

        public static List<Finding> Check(BootImageAnalysis image, string partition, DeviceFacts phone, bool phoneHasInitBoot)
        {
            List<Finding> findings = new List<Finding>();
            string target = BaseName(partition);
            if (!Applies(partition))
                return findings;

            if (image.Kind == BootImageKind.NotBootImage)
            {
                findings.Add(new Finding(FindingLevel.Block, FindingCode.NotBootImage, partition));
                return findings;
            }
            bool vendorTarget = target == "vendor_boot" || target == "vendor_kernel_boot";
            if (vendorTarget != (image.Kind == BootImageKind.VendorBoot))
            {
                findings.Add(new Finding(FindingLevel.Block, FindingCode.VendorBootMismatch, partition));
                return findings;
            }
            if (image.Kind == BootImageKind.VendorBoot)
                return findings;

            if ((target == "boot" || target == "recovery") && image.Kind == BootImageKind.NoKernel)
                findings.Add(new Finding(FindingLevel.Block, FindingCode.NoKernelForBoot, partition,
                    phoneHasInitBoot ? "init_boot" : ""));
            if (target == "init_boot" && image.Kind == BootImageKind.Bootable)
                findings.Add(new Finding(FindingLevel.Warn, FindingCode.KernelInInitBoot, partition));

            KernelInfo kernel = image.Kernel;
            KernelInfo running = phone?.Kernel != null ? KernelInfo.FromRelease(phone.Kernel) : null;
            if (kernel?.Release != null && running != null && target == "boot")
            {
                if (kernel.Kmi != null && running.Kmi != null && kernel.Kmi != running.Kmi)
                    findings.Add(new Finding(FindingLevel.Block, FindingCode.KmiMismatch, kernel.Kmi, running.Kmi));
                else if (kernel.Version != null && running.Version != null && kernel.Version != running.Version)
                    findings.Add(new Finding(FindingLevel.Warn, FindingCode.KernelVersionMismatch, kernel.Version, running.Version));
            }

            string phonePatch = Month(phone?.Patch);
            if (image.PatchLevel != null && phonePatch != null && target != "recovery"
                && string.CompareOrdinal(image.PatchLevel, phonePatch) < 0)
                findings.Add(new Finding(FindingLevel.Warn, FindingCode.OlderPatchLevel, image.PatchLevel, phonePatch));

            if (kernel?.Release != null)
                findings.Add(new Finding(FindingLevel.Info, FindingCode.KernelSummary, kernel.Release, kernel.Kmi ?? ""));
            RootKind root = image.Root;
            RootKind phoneRoot = RootFromName(phone?.Root);
            if (root != RootKind.None && phoneRoot != RootKind.None && (root & phoneRoot) == 0)
                findings.Add(new Finding(FindingLevel.Warn, FindingCode.RootManagerChange,
                    RootName(root, false), RootName(phoneRoot, false)));
            if (root != RootKind.None || (kernel?.SuSFS ?? false))
                findings.Add(new Finding(FindingLevel.Info, FindingCode.RootSummary, RootName(root, kernel?.SuSFS ?? false)));
            else if ((kernel?.Readable ?? false) || (image.Ramdisk?.Readable ?? false))
                findings.Add(new Finding(FindingLevel.Info, FindingCode.Stock));
            return findings;
        }

        /// <summary>The root manager the Companion named ("KernelSU"), as a RootKind.</summary>
        public static RootKind RootFromName(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "magisk": return RootKind.Magisk;
                case "kernelsu": return RootKind.KernelSU;
                case "apatch": return RootKind.APatch;
                default: return RootKind.None;
            }
        }

        /// <summary>"2024-09-05" → "2024-09".</summary>
        static string Month(string date)
        {
            return date != null && date.Length >= 7 && date[4] == '-' ? date.Substring(0, 7) : null;
        }

        public static string RootName(RootKind root, bool susfs)
        {
            List<string> parts = new List<string>();
            if ((root & RootKind.Magisk) != 0) parts.Add("Magisk");
            if ((root & RootKind.KernelSU) != 0) parts.Add("KernelSU");
            if ((root & RootKind.APatch) != 0) parts.Add("APatch");
            if (susfs) parts.Add("SuSFS");
            return string.Join(" + ", parts);
        }

        public static FindingLevel Worst(IEnumerable<Finding> findings)
        {
            FindingLevel worst = FindingLevel.Info;
            foreach (Finding f in findings)
                if (f.Level > worst)
                    worst = f.Level;
            return worst;
        }
    }
}
