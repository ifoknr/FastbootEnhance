using System;
using System.Collections.Generic;
using System.Globalization;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.Core.Adb
{
    public enum RootTool
    {
        Magisk,
        KernelSU,
        APatch,
    }

    /// <summary>A patched image found on the phone.</summary>
    public sealed class PatchedImage
    {
        public string Path { get; internal set; }
        public string Name { get; internal set; }
        public long Size { get; internal set; }
        public DateTime Modified { get; internal set; }
    }

    public enum PatchProblem
    {
        /// <summary>No root found in the patched image: the root app did not patch it, or patched something else.</summary>
        NotRooted,

        /// <summary>The original had a kernel and this one does not (or the other way round).</summary>
        DifferentKind,

        /// <summary>Magisk and KernelSU (LKM) keep the kernel; a different one means a different image was patched.</summary>
        DifferentKernel,
    }

    /// <summary>
    /// The steps of rooting with Magisk, KernelSU or APatch, as far as they do not need the
    /// phone's screen: which image to patch, where it goes on the phone, how to find what the
    /// root app wrote back, and whether that is the right image.
    /// </summary>
    public static class RootHelper
    {
        /// <summary>The folder the root apps open first and save their patched image to.</summary>
        public const string PhoneFolder = "/sdcard/Download";

        /// <summary>
        /// Lists the patched images in the Download folder as "mtime|size|path". Magisk writes
        /// magisk_patched-*.img, KernelSU kernelsu_*patched*.img, APatch apatch_patched_*.img.
        /// </summary>
        public const string ListPatchedCommand = "stat -c %Y\\|%s\\|%n -- " + PhoneFolder + "/*patched*.img 2>/dev/null";

        /// <summary>
        /// The partition the root app patches: APatch patches the kernel (boot); Magisk and
        /// KernelSU in LKM mode patch the ramdisk, which is in init_boot on phones that have it
        /// (Android 13 and later, GKI) and in boot otherwise.
        /// </summary>
        public static string PartitionFor(RootTool tool, bool hasInitBoot)
        {
            return tool == RootTool.APatch || !hasInitBoot ? "boot" : "init_boot";
        }

        /// <summary>The name the stock image gets on the phone, kept apart from the user's files.</summary>
        public static string PhoneName(string partition)
        {
            return "fbstudio_" + partition + ".img";
        }

        public static string PhonePath(string partition)
        {
            return DeviceEntry.Combine(PhoneFolder, PhoneName(partition));
        }

        /// <summary>The newest patched image in a ListPatchedCommand listing, made after notBefore.</summary>
        public static PatchedImage FindNewest(string listing, DateTime notBefore)
        {
            PatchedImage newest = null;
            foreach (string raw in (listing ?? "").Split('\n'))
            {
                string[] parts = raw.TrimEnd('\r').Split(new[] { '|' }, 3);
                long seconds, size;
                if (parts.Length < 3 || parts[2].Length == 0
                    || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
                    || size <= 0)
                    continue;
                DateTime modified = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                if (modified < notBefore.ToUniversalTime())
                    continue;
                string name = parts[2].Substring(parts[2].LastIndexOf('/') + 1);
                if (name.StartsWith("fbstudio_", StringComparison.Ordinal))
                    continue; // the stock image this app sent
                if (newest == null || modified > newest.Modified)
                    newest = new PatchedImage { Path = parts[2], Name = name, Size = size, Modified = modified };
            }
            return newest;
        }

        /// <summary>What is wrong with the patched image, compared with the stock one sent to the phone.</summary>
        public static List<PatchProblem> Verify(BootImageAnalysis stock, BootImageAnalysis patched, RootTool tool)
        {
            List<PatchProblem> problems = new List<PatchProblem>();
            if (patched.Kind != stock.Kind)
                problems.Add(PatchProblem.DifferentKind);
            if (patched.Root == RootKind.None)
                problems.Add(PatchProblem.NotRooted);
            if (tool != RootTool.APatch && stock.Kernel?.Release != null && patched.Kernel?.Release != null
                && stock.Kernel.Release != patched.Kernel.Release)
                problems.Add(PatchProblem.DifferentKernel);
            return problems;
        }

        /// <summary>The root a patched image should carry for each app.</summary>
        public static RootKind Expected(RootTool tool)
        {
            switch (tool)
            {
                case RootTool.Magisk: return RootKind.Magisk;
                case RootTool.KernelSU: return RootKind.KernelSU;
                default: return RootKind.APatch;
            }
        }
    }
}
