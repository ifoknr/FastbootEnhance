using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Images
{
    [Flags]
    public enum RootKind
    {
        None = 0,
        Magisk = 1,
        KernelSU = 2,
        APatch = 4,
    }

    /// <summary>The kernel inside a boot image.</summary>
    public sealed class KernelInfo
    {
        public CompressionKind Compression { get; internal set; }

        /// <summary>False when the compression is one this app cannot read (zstd, xz, ...).</summary>
        public bool Readable { get; internal set; }

        /// <summary>The "Linux version ..." line, or null when none was found.</summary>
        public string VersionLine { get; internal set; }

        /// <summary>The kernel release, e.g. "5.10.198-android12-9-00001-gabc123".</summary>
        public string Release { get; internal set; }

        /// <summary>"5.10" from the release.</summary>
        public string Version { get; internal set; }

        /// <summary>
        /// The kernel module interface of a GKI kernel, e.g. "android12-5.10"; null for kernels
        /// that are not GKI. Kernels with different KMIs cannot replace each other.
        /// </summary>
        public string Kmi { get; internal set; }

        public RootKind Root { get; internal set; }

        /// <summary>Built with SuSFS (root hiding at the kernel level).</summary>
        public bool SuSFS { get; internal set; }

        static readonly Regex ReleasePattern = new Regex(@"^(\d+)\.(\d+)(?:\.\d+)?(?:-(android\d+)-\d+)?", RegexOptions.CultureInvariant);

        /// <summary>Version and KMI from a kernel release string such as "uname -r" prints.</summary>
        public static KernelInfo FromRelease(string release)
        {
            KernelInfo info = new KernelInfo { Readable = true, Release = release?.Trim() };
            if (string.IsNullOrEmpty(info.Release))
                return info;
            Match m = ReleasePattern.Match(info.Release);
            if (m.Success)
            {
                info.Version = m.Groups[1].Value + "." + m.Groups[2].Value;
                if (m.Groups[3].Success)
                    info.Kmi = m.Groups[3].Value + "-" + info.Version;
            }
            return info;
        }
    }

    /// <summary>The ramdisk inside a boot image.</summary>
    public sealed class RamdiskInfo
    {
        public CompressionKind Compression { get; internal set; }
        public bool Readable { get; internal set; }
        public int FileCount { get; internal set; }
        public RootKind Root { get; internal set; }
    }

    /// <summary>
    /// Everything a boot, init_boot or recovery image tells about itself: header, patch level,
    /// kernel version and KMI, and whether Magisk, KernelSU or APatch patched it.
    /// </summary>
    public sealed class BootImageAnalysis
    {
        /// <summary>Kernels and ramdisks larger than this are not unpacked.</summary>
        public const int MaxUnpacked = 160 << 20;

        BootImageAnalysis()
        {
        }

        public BootImageKind Kind { get; private set; }
        public int HeaderVersion { get; private set; } = -1;
        public int PageSize { get; private set; }

        /// <summary>Android version the image was built for, e.g. "14.0.0"; null when not set.</summary>
        public string OsVersion { get; private set; }

        /// <summary>Security patch level, "2024-09"; from the header, else from the AVB footer.</summary>
        public string PatchLevel { get; private set; }

        public string Cmdline { get; private set; }

        /// <summary>The image ends in an AVB footer (it carries its own hash for verified boot).</summary>
        public bool HasAvbFooter { get; private set; }

        public KernelInfo Kernel { get; private set; }
        public RamdiskInfo Ramdisk { get; private set; }

        public RootKind Root => (Kernel?.Root ?? RootKind.None) | (Ramdisk?.Root ?? RootKind.None);

        public static BootImageAnalysis Analyze(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess))
                return Analyze(file);
        }

        public static BootImageAnalysis Analyze(Stream stream)
        {
            BootImageHeader header = BootImageHeader.Read(stream);
            BootImageAnalysis a = new BootImageAnalysis { Kind = header.Kind, HeaderVersion = header.HeaderVersion };
            if (header.Kind != BootImageKind.Bootable && header.Kind != BootImageKind.NoKernel)
                return a;

            byte[] head = ReadAt(stream, 0, (int)Math.Min(stream.Length, 1660));
            int version = header.HeaderVersion;
            uint osVersion;
            long kernelSize = header.KernelSize, ramdiskSize = header.RamdiskSize;
            if (version >= 3)
            {
                a.PageSize = 4096;
                osVersion = BitConverter.ToUInt32(head, 16);
                a.Cmdline = CString(head, 44, 1536);
            }
            else
            {
                a.PageSize = (int)BitConverter.ToUInt32(head, 36);
                if (a.PageSize < 2048 || a.PageSize > 65536 || (a.PageSize & (a.PageSize - 1)) != 0)
                    a.PageSize = 2048;
                osVersion = BitConverter.ToUInt32(head, 44);
                a.Cmdline = (CString(head, 64, 512) + " " + CString(head, 608, 1024)).Trim();
            }
            DecodeOsVersion(osVersion, a);
            ReadAvbFooter(stream, a);

            long kernelOffset = a.PageSize;
            long ramdiskOffset = kernelOffset + Align(kernelSize, a.PageSize);
            if (kernelSize > 0)
                a.Kernel = ReadKernel(stream, kernelOffset, kernelSize);
            if (ramdiskSize > 0)
                a.Ramdisk = ReadRamdisk(stream, ramdiskOffset, ramdiskSize);
            return a;
        }

        // os_version: the Android version in the top 21 bits (7 bits each), the patch level in
        // the low 11 (year - 2000 in 7 bits, month in 4).
        static void DecodeOsVersion(uint value, BootImageAnalysis a)
        {
            if (value == 0)
                return;
            uint os = value >> 11, level = value & 0x7FF;
            if (os != 0)
                a.OsVersion = ((os >> 14) & 0x7F) + "." + ((os >> 7) & 0x7F) + "." + (os & 0x7F);
            int month = (int)(level & 0xF);
            if (level != 0 && month >= 1 && month <= 12)
                a.PatchLevel = (2000 + (level >> 4)).ToString("D4") + "-" + month.ToString("D2");
        }

        /// <summary>
        /// GKI images (boot v4, init_boot) leave the header's os_version empty and keep the
        /// patch level as a property of their AVB hash descriptor:
        /// com.android.build.boot.security_patch = 2024-09-05.
        /// </summary>
        static void ReadAvbFooter(Stream stream, BootImageAnalysis a)
        {
            if (stream.Length < 64)
                return;
            byte[] footer = ReadAt(stream, stream.Length - 64, 64);
            if (footer == null || Encoding.ASCII.GetString(footer, 0, 4) != "AVBf")
                return;
            a.HasAvbFooter = true;
            long offset = BigEndian64(footer, 20), size = BigEndian64(footer, 28);
            if (offset <= 0 || size <= 0 || size > (1 << 20) || offset + size > stream.Length)
                return;
            byte[] vbmeta = ReadAt(stream, offset, (int)size);
            if (vbmeta == null)
                return;
            if (a.PatchLevel == null)
            {
                string patch = AvbProperty(vbmeta, ".security_patch");
                if (patch != null && patch.Length >= 7)
                    a.PatchLevel = patch.Substring(0, 7);
            }
            if (a.OsVersion == null)
                a.OsVersion = AvbProperty(vbmeta, ".os_version");
        }

        // Property descriptors hold "key\0value\0"; the key we want ends with suffix.
        static string AvbProperty(byte[] vbmeta, string suffix)
        {
            byte[] prefix = Encoding.ASCII.GetBytes("com.android.build.");
            for (int i = IndexOf(vbmeta, prefix, 0); i >= 0; i = IndexOf(vbmeta, prefix, i + 1))
            {
                int keyEnd = Array.IndexOf(vbmeta, (byte)0, i);
                if (keyEnd < 0)
                    break;
                string key = Encoding.ASCII.GetString(vbmeta, i, keyEnd - i);
                if (!key.EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                int valueEnd = Array.IndexOf(vbmeta, (byte)0, keyEnd + 1);
                if (valueEnd < 0)
                    break;
                return Encoding.ASCII.GetString(vbmeta, keyEnd + 1, valueEnd - keyEnd - 1);
            }
            return null;
        }

        static KernelInfo ReadKernel(Stream stream, long offset, long size)
        {
            KernelInfo info = new KernelInfo();
            byte[] raw = ReadAt(stream, offset, (int)Math.Min(size, MaxUnpacked));
            if (raw == null)
                return info;
            info.Compression = Compression.Detect(raw, 0, raw.Length);
            byte[] kernel;
            try
            {
                kernel = info.Compression == CompressionKind.Unknown
                    ? raw // try the bytes as they are: some kernels carry a header of their own
                    : Compression.Decompress(raw, 0, raw.Length, info.Compression, MaxUnpacked);
            }
            catch (InvalidDataException)
            {
                kernel = null;
            }
            if (kernel == null)
                return info;

            string line = FindVersionLine(kernel);
            info.Readable = line != null || info.Compression != CompressionKind.Unknown;
            if (line != null)
            {
                KernelInfo parsed = KernelInfo.FromRelease(line.Substring("Linux version ".Length).Split(' ')[0]);
                info.VersionLine = line;
                info.Release = parsed.Release;
                info.Version = parsed.Version;
                info.Kmi = parsed.Kmi;
            }
            if (Contains(kernel, "KernelSU"))
                info.Root |= RootKind.KernelSU;
            if (Contains(kernel, "KernelPatch") || Contains(kernel, "kpimg"))
                info.Root |= RootKind.APatch;
            info.SuSFS = Contains(kernel, "susfs");
            return info;
        }

        static RamdiskInfo ReadRamdisk(Stream stream, long offset, long size)
        {
            RamdiskInfo info = new RamdiskInfo();
            byte[] raw = ReadAt(stream, offset, (int)Math.Min(size, MaxUnpacked));
            if (raw == null)
                return info;
            info.Compression = Compression.Detect(raw, 0, raw.Length);
            byte[] cpio;
            try
            {
                cpio = Compression.Decompress(raw, 0, raw.Length, info.Compression, MaxUnpacked);
            }
            catch (InvalidDataException)
            {
                cpio = null;
            }
            if (cpio == null)
                return info;

            List<string> names = Cpio.ListNames(cpio);
            info.Readable = names.Count > 0;
            info.FileCount = names.Count;
            foreach (string name in names)
            {
                if (name == ".backup/.magisk" || name.StartsWith("overlay.d/sbin/magisk", StringComparison.Ordinal))
                    info.Root |= RootKind.Magisk;
                else if (name == "kernelsu.ko" || name.EndsWith("/kernelsu.ko", StringComparison.Ordinal))
                    info.Root |= RootKind.KernelSU;
            }
            return info;
        }

        static string FindVersionLine(byte[] kernel)
        {
            byte[] marker = Encoding.ASCII.GetBytes("Linux version ");
            for (int i = IndexOf(kernel, marker, 0); i >= 0; i = IndexOf(kernel, marker, i + 1))
            {
                int end = i;
                while (end < kernel.Length && kernel[end] != 0 && kernel[end] != '\n' && end - i < 512)
                    end++;
                string line = Encoding.ASCII.GetString(kernel, i, end - i);
                // The real banner has a release right after it; format strings ("%s") do not.
                if (line.Length > marker.Length && char.IsDigit(line[marker.Length]))
                    return line;
            }
            return null;
        }

        static bool Contains(byte[] data, string text)
        {
            return IndexOf(data, Encoding.ASCII.GetBytes(text), 0) >= 0;
        }

        static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            return data.AsSpan(from).IndexOf(pattern) is int i && i >= 0 ? from + i : -1;
        }

        static string CString(byte[] data, int start, int max)
        {
            if (data.Length < start)
                return "";
            int length = Math.Min(max, data.Length - start);
            int end = Array.IndexOf(data, (byte)0, start, length);
            return Encoding.ASCII.GetString(data, start, (end < 0 ? start + length : end) - start).Trim();
        }

        static long Align(long value, int page)
        {
            return (value + page - 1) / page * page;
        }

        static long BigEndian64(byte[] data, int at)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | data[at + i];
            return value;
        }

        static byte[] ReadAt(Stream stream, long offset, int count)
        {
            if (count <= 0 || offset < 0 || offset >= stream.Length)
                return null;
            count = (int)Math.Min(count, stream.Length - offset);
            byte[] data = new byte[count];
            stream.Position = offset;
            return StreamUtil.TryReadExactly(stream, data) ? data : null;
        }
    }

    /// <summary>The file names in a "newc" cpio archive (what Android ramdisks are).</summary>
    public static class Cpio
    {
        public static List<string> ListNames(byte[] data, int limit = 100000)
        {
            List<string> names = new List<string>();
            int pos = 0;
            while (pos + 110 <= data.Length && names.Count < limit)
            {
                string magic = Encoding.ASCII.GetString(data, pos, 6);
                if (magic != "070701" && magic != "070702")
                    break;
                int fileSize = Hex(data, pos + 54);
                int nameSize = Hex(data, pos + 94);
                if (fileSize < 0 || nameSize <= 0 || pos + 110 + nameSize > data.Length)
                    break;
                string name = Encoding.UTF8.GetString(data, pos + 110, nameSize - 1);
                if (name == "TRAILER!!!")
                {
                    // Several archives can follow each other (Android concatenates ramdisks).
                    pos = Align4(pos + 110 + nameSize) + Align4(fileSize);
                    while (pos < data.Length && data[pos] == 0)
                        pos++;
                    continue;
                }
                names.Add(name.StartsWith("./", StringComparison.Ordinal) ? name.Substring(2) : name);
                pos = Align4(Align4(pos + 110 + nameSize) + fileSize);
            }
            return names;
        }

        static int Align4(int value) => (value + 3) & ~3;

        static int Hex(byte[] data, int at)
        {
            int value = 0;
            for (int i = 0; i < 8; i++)
            {
                int c = data[at + i];
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0)
                    return -1;
                value = (value << 4) | digit;
            }
            return value;
        }
    }
}
