using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FastbootEnhance.Core.Adb;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Images;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public class BootImageTests
    {
        const string Banner = "Linux version 5.10.198-android12-9-00001-gabcdef (builder@host) #1 SMP PREEMPT\n";
        static readonly byte[] KernelSuMarker = Encoding.ASCII.GetBytes("KernelSU\0susfs_init\0");

        /// <summary>
        /// A stand-in arm64 kernel: the Image header ("ARMd" at 56), the version banner, some
        /// marker strings, then bytes that compress with literals and with overlapping matches.
        /// CompressedFixtures holds this exact blob (with KernelSuMarker) compressed by real tools.
        /// </summary>
        public static byte[] KernelBlob(byte[] extra, string banner = Banner)
        {
            MemoryStream ms = new MemoryStream();
            byte[] header = new byte[64];
            Encoding.ASCII.GetBytes("ARMd").CopyTo(header, 56);
            ms.Write(header, 0, header.Length);
            byte[] text = Encoding.ASCII.GetBytes(banner);
            ms.Write(text, 0, text.Length);
            ms.WriteByte(0);
            ms.Write(extra, 0, extra.Length);
            for (int i = 0; i < 4096; i++)
                ms.WriteByte((byte)(i * 7 % 251));
            for (int i = 0; i < 1024; i++)
                ms.Write(Encoding.ASCII.GetBytes("abcd"), 0, 4);
            return ms.ToArray();
        }

        static byte[] Gzip(byte[] data)
        {
            MemoryStream ms = new MemoryStream();
            using (GZipStream gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                gz.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        /// <summary>A newc cpio archive with these files (empty contents).</summary>
        static byte[] Cpio(params string[] names)
        {
            MemoryStream ms = new MemoryStream();
            void Entry(string name, int mode)
            {
                byte[] n = Encoding.ASCII.GetBytes(name + "\0");
                string head = "070701" + "00000001" + mode.ToString("X8") + "00000000" + "00000000" + "00000001"
                    + "00000000" + "00000000" + "00000000" + "00000000" + "00000000" + "00000000"
                    + n.Length.ToString("X8") + "00000000";
                ms.Write(Encoding.ASCII.GetBytes(head), 0, 110);
                ms.Write(n, 0, n.Length);
                while (ms.Length % 4 != 0)
                    ms.WriteByte(0);
            }
            foreach (string name in names)
                Entry(name, 0x81A4);
            Entry("TRAILER!!!", 0);
            return ms.ToArray();
        }

        static uint OsVersion(int a, int b, int c, int year, int month)
        {
            return (uint)((((a << 14) | (b << 7) | c) << 11) | ((year - 2000) << 4) | month);
        }

        /// <summary>A boot image: header v2 (page 2048) or v4 (page 4096) with kernel and ramdisk.</summary>
        static byte[] BootImage(int version, byte[] kernel, byte[] ramdisk, uint osVersion = 0, Dictionary<string, string> avb = null)
        {
            int page = version >= 3 ? 4096 : 2048;
            byte[] head = new byte[page];
            Encoding.ASCII.GetBytes("ANDROID!").CopyTo(head, 0);
            BitConverter.GetBytes((uint)kernel.Length).CopyTo(head, 8);
            if (version >= 3)
            {
                BitConverter.GetBytes((uint)ramdisk.Length).CopyTo(head, 12);
                BitConverter.GetBytes(osVersion).CopyTo(head, 16);
                Encoding.ASCII.GetBytes("console=ttyMSM0").CopyTo(head, 44);
            }
            else
            {
                BitConverter.GetBytes((uint)ramdisk.Length).CopyTo(head, 16);
                BitConverter.GetBytes((uint)page).CopyTo(head, 36);
                BitConverter.GetBytes(osVersion).CopyTo(head, 44);
                Encoding.ASCII.GetBytes("androidboot.hardware=qcom").CopyTo(head, 64);
            }
            BitConverter.GetBytes((uint)version).CopyTo(head, 40);

            MemoryStream ms = new MemoryStream();
            ms.Write(head, 0, head.Length);
            void Padded(byte[] part)
            {
                ms.Write(part, 0, part.Length);
                while (ms.Length % page != 0)
                    ms.WriteByte(0);
            }
            Padded(kernel);
            Padded(ramdisk);

            if (avb != null)
            {
                // A vbmeta blob holding property descriptors, then the AVB footer at the end.
                long vbmetaOffset = ms.Length;
                MemoryStream vbmeta = new MemoryStream();
                vbmeta.Write(Encoding.ASCII.GetBytes("AVB0"), 0, 4);
                vbmeta.Write(new byte[252], 0, 252);
                foreach (KeyValuePair<string, string> p in avb)
                {
                    byte[] kv = Encoding.ASCII.GetBytes(p.Key + "\0" + p.Value + "\0");
                    vbmeta.Write(new byte[32], 0, 32); // tag, sizes
                    vbmeta.Write(kv, 0, kv.Length);
                }
                byte[] blob = vbmeta.ToArray();
                ms.Write(blob, 0, blob.Length);
                while (ms.Length % 4096 != 0)
                    ms.WriteByte(0);
                byte[] footer = new byte[64];
                Encoding.ASCII.GetBytes("AVBf").CopyTo(footer, 0);
                BigEndian(vbmetaOffset, footer, 20);
                BigEndian(blob.Length, footer, 28);
                ms.Write(footer, 0, footer.Length);
            }
            return ms.ToArray();
        }

        static void BigEndian(long value, byte[] into, int at)
        {
            for (int i = 7; i >= 0; i--)
            {
                into[at + i] = (byte)value;
                value >>= 8;
            }
        }

        static BootImageAnalysis Analyze(byte[] image) => BootImageAnalysis.Analyze(new MemoryStream(image));

        [Theory]
        [InlineData(nameof(CompressedFixtures.Lz4Legacy), CompressionKind.Lz4Legacy)]
        [InlineData(nameof(CompressedFixtures.Lz4Frame), CompressionKind.Lz4Frame)]
        [InlineData(nameof(CompressedFixtures.Lz4FrameChecksums), CompressionKind.Lz4Frame)]
        [InlineData(nameof(CompressedFixtures.Xz), CompressionKind.Xz)]
        public void Kernels_compressed_by_real_tools_unpack_to_the_original(string fixture, CompressionKind kind)
        {
            byte[] packed = Convert.FromBase64String((string)typeof(CompressedFixtures).GetField(fixture).GetValue(null));
            Assert.Equal(kind, Compression.Detect(packed, 0, packed.Length));
            Assert.Equal(KernelBlob(KernelSuMarker), Compression.Decompress(packed, 0, packed.Length, kind, 1 << 20));
        }

        /// <summary>6000 pseudo-random bytes repeated 15 times: 90000 bytes, more than one 64 KiB block.</summary>
        public static byte[] LinkedBlob()
        {
            byte[] chunk = new byte[6000];
            long x = 12345;
            for (int i = 0; i < chunk.Length; i++)
            {
                x = (x * 1103515245 + 12345) & 0x7FFFFFFF;
                chunk[i] = (byte)((x >> 16) & 0xFF);
            }
            return Enumerable.Repeat(chunk, 15).SelectMany(c => c).ToArray();
        }

        [Fact]
        public void A_linked_lz4_frame_reaches_back_into_earlier_blocks()
        {
            byte[] packed = Convert.FromBase64String(CompressedFixtures.Lz4FrameLinked);
            Assert.Equal(LinkedBlob(), Compression.Decompress(packed, 0, packed.Length, CompressionKind.Lz4Frame, 1 << 20));
        }

        [Fact]
        public void Zstd_and_gzip_unpack_too_and_trailing_bytes_are_ignored()
        {
            byte[] blob = KernelBlob(KernelSuMarker);
            byte[] zstd = new ZstdSharp.Compressor(3).Wrap(blob).ToArray();
            Assert.Equal(CompressionKind.Zstd, Compression.Detect(zstd, 0, zstd.Length));
            Assert.Equal(blob, Compression.Decompress(zstd, 0, zstd.Length, CompressionKind.Zstd, 1 << 20));

            // Image.gz-dtb: a DTB is appended after the gzip member.
            byte[] gz = Gzip(blob).Concat(new byte[] { 0xD0, 0x0D, 0xFE, 0xED, 1, 2, 3, 4 }).ToArray();
            Assert.Equal(blob, Compression.Decompress(gz, 0, gz.Length, CompressionKind.Gzip, 1 << 20));
        }

        [Fact]
        public void Output_larger_than_the_limit_is_refused()
        {
            byte[] packed = Convert.FromBase64String(CompressedFixtures.Lz4Legacy);
            Assert.Throws<InvalidDataException>(() => Compression.Decompress(packed, 0, packed.Length, CompressionKind.Lz4Legacy, 1000));
            byte[] gz = Gzip(new byte[100000]);
            Assert.Throws<InvalidDataException>(() => Compression.Decompress(gz, 0, gz.Length, CompressionKind.Gzip, 1000));
        }

        [Fact]
        public void Damaged_lz4_is_reported_not_crashed()
        {
            byte[] packed = Convert.FromBase64String(CompressedFixtures.Lz4Frame);
            for (int i = 20; i < packed.Length - 8; i += 7)
                packed[i] ^= 0x5A;
            try
            {
                Compression.Decompress(packed, 0, packed.Length, CompressionKind.Lz4Frame, 1 << 20);
            }
            catch (InvalidDataException)
            {
            }
        }

        [Fact]
        public void A_v2_boot_image_tells_kernel_kmi_patch_level_and_root()
        {
            byte[] kernel = Convert.FromBase64String(CompressedFixtures.Lz4Legacy);
            byte[] ramdisk = Gzip(Cpio("init", "system/bin/init", ".backup/.magisk", ".backup/init", "overlay.d/sbin/magisk64.xz"));
            BootImageAnalysis a = Analyze(BootImage(2, kernel, ramdisk, OsVersion(12, 0, 0, 2024, 9)));

            Assert.Equal(BootImageKind.Bootable, a.Kind);
            Assert.Equal(2, a.HeaderVersion);
            Assert.Equal(2048, a.PageSize);
            Assert.Equal("12.0.0", a.OsVersion);
            Assert.Equal("2024-09", a.PatchLevel);
            Assert.Equal("androidboot.hardware=qcom", a.Cmdline);
            Assert.Equal(CompressionKind.Lz4Legacy, a.Kernel.Compression);
            Assert.Equal("5.10.198-android12-9-00001-gabcdef", a.Kernel.Release);
            Assert.Equal("5.10", a.Kernel.Version);
            Assert.Equal("android12-5.10", a.Kernel.Kmi);
            Assert.True(a.Kernel.SuSFS);
            Assert.Equal(CompressionKind.Gzip, a.Ramdisk.Compression);
            Assert.Equal(5, a.Ramdisk.FileCount);
            Assert.Equal(RootKind.Magisk | RootKind.KernelSU, a.Root);
        }

        [Fact]
        public void A_gki_boot_image_takes_its_patch_level_from_the_avb_footer()
        {
            byte[] kernel = KernelBlob(new byte[0], "Linux version 6.1.75-android14-11-g1234 (build@ci) #1 SMP PREEMPT\n");
            BootImageAnalysis a = Analyze(BootImage(4, kernel, new byte[0], 0, new Dictionary<string, string>
            {
                ["com.android.build.boot.fingerprint"] = "google/gki/x:14/AP1A/1:user/release-keys",
                ["com.android.build.boot.os_version"] = "14",
                ["com.android.build.boot.security_patch"] = "2024-03-05",
            }));
            Assert.True(a.HasAvbFooter);
            Assert.Equal(4096, a.PageSize);
            Assert.Equal("2024-03", a.PatchLevel);
            Assert.Equal("14", a.OsVersion);
            Assert.Equal(CompressionKind.None, a.Kernel.Compression);
            Assert.Equal("android14-6.1", a.Kernel.Kmi);
            Assert.Equal(RootKind.None, a.Root);
            Assert.Null(a.Ramdisk);
        }

        [Fact]
        public void An_init_boot_patched_by_kernelsu_lkm_is_recognised()
        {
            byte[] ramdisk = Gzip(Cpio("init", "init.real", "kernelsu.ko"));
            BootImageAnalysis a = Analyze(BootImage(4, new byte[0], ramdisk));
            Assert.Equal(BootImageKind.NoKernel, a.Kind);
            Assert.Null(a.Kernel);
            Assert.Equal(RootKind.KernelSU, a.Root);
        }

        [Fact]
        public void Unknown_data_is_not_mistaken_for_a_kernel()
        {
            byte[] noise = new byte[8192];
            new Random(5).NextBytes(noise);
            noise[0] = 0x11; // not a known magic
            BootImageAnalysis a = Analyze(BootImage(2, noise, Gzip(Cpio("init"))));
            Assert.Null(a.Kernel.Release);
            Assert.False(a.Kernel.Readable);
            Assert.Equal(RootKind.None, a.Root);
        }

        [Fact]
        public void Not_a_boot_image_is_said_so()
        {
            Assert.Equal(BootImageKind.NotBootImage, Analyze(new byte[4096]).Kind);
        }

        [Theory]
        [InlineData("5.10.198-android12-9-00001-gabc", "5.10", "android12-5.10")]
        [InlineData("6.1.75-android14-11-g1234", "6.1", "android14-6.1")]
        [InlineData("4.19.157-perf+", "4.19", null)]
        [InlineData("5.4.254-qgki-g0fba", "5.4", null)]
        public void Kernel_release_strings_are_parsed(string release, string version, string kmi)
        {
            KernelInfo k = KernelInfo.FromRelease(release);
            Assert.Equal(version, k.Version);
            Assert.Equal(kmi, k.Kmi);
        }

        // ---------- what the flash check says ----------

        static BootImageAnalysis Gki(string release, string patch = "2024-09", bool kernel = true)
        {
            byte[] k = kernel ? KernelBlob(new byte[0], "Linux version " + release + " (b@h) #1 SMP\n") : new byte[0];
            byte[] r = kernel ? new byte[0] : Gzip(Cpio("init"));
            return Analyze(BootImage(4, k, r, 0, new Dictionary<string, string>
            {
                ["com.android.build.boot.security_patch"] = patch + "-01",
            }));
        }

        static DeviceFacts Phone(string kernel = "5.10.198-android12-9-g1", string patch = "2024-09-05")
        {
            return new DeviceFacts { Serial = "ABC", Kernel = kernel, Patch = patch, Seen = DateTime.UtcNow };
        }

        static FindingCode[] Codes(List<Finding> findings, FindingLevel level)
        {
            return findings.Where(f => f.Level == level).Select(f => f.Code).ToArray();
        }

        [Fact]
        public void A_matching_kernel_flashes_without_questions()
        {
            List<Finding> f = BootFlashCheck.Check(Gki("5.10.205-android12-9-g9"), "boot_a", Phone(), true);
            Assert.Equal(FindingLevel.Info, BootFlashCheck.Worst(f));
            Assert.Contains(FindingCode.KernelSummary, Codes(f, FindingLevel.Info));
            Assert.Contains(FindingCode.Stock, Codes(f, FindingLevel.Info));
        }

        [Fact]
        public void A_kernel_for_another_kmi_is_blocked()
        {
            List<Finding> f = BootFlashCheck.Check(Gki("5.15.123-android13-8-g1"), "boot", Phone(), true);
            Finding kmi = Assert.Single(f, x => x.Code == FindingCode.KmiMismatch);
            Assert.Equal(FindingLevel.Block, kmi.Level);
            Assert.Equal(new[] { "android13-5.15", "android12-5.10" }, kmi.Values);
        }

        [Fact]
        public void Without_adb_facts_the_kernel_is_not_compared()
        {
            List<Finding> f = BootFlashCheck.Check(Gki("5.15.123-android13-8-g1"), "boot", null, true);
            Assert.Equal(FindingLevel.Info, BootFlashCheck.Worst(f));
        }

        [Fact]
        public void An_init_boot_image_in_boot_is_blocked_and_the_reverse_asked()
        {
            List<Finding> f = BootFlashCheck.Check(Gki("x", kernel: false), "boot_b", Phone(), true);
            Finding noKernel = Assert.Single(f, x => x.Code == FindingCode.NoKernelForBoot);
            Assert.Equal(FindingLevel.Block, noKernel.Level);
            Assert.Equal("init_boot", noKernel.Values[1]);

            List<Finding> reverse = BootFlashCheck.Check(Gki("5.10.205-android12-9-g9"), "init_boot_a", Phone(), true);
            Assert.Contains(FindingCode.KernelInInitBoot, Codes(reverse, FindingLevel.Warn));
        }

        [Fact]
        public void An_older_patch_level_is_asked_about()
        {
            List<Finding> f = BootFlashCheck.Check(Gki("5.10.205-android12-9-g9", "2024-03"), "boot", Phone(), true);
            Finding older = Assert.Single(f, x => x.Code == FindingCode.OlderPatchLevel);
            Assert.Equal(FindingLevel.Warn, older.Level);
            Assert.Equal(new[] { "2024-03", "2024-09" }, older.Values);
        }

        [Fact]
        public void Wrong_kinds_of_file_are_blocked_and_other_partitions_are_left_alone()
        {
            Assert.Equal(FindingCode.NotBootImage, Assert.Single(BootFlashCheck.Check(Analyze(new byte[4096]), "boot", null, false)).Code);
            Assert.Equal(FindingCode.VendorBootMismatch,
                Assert.Single(BootFlashCheck.Check(Gki("5.10.1-android12-9"), "vendor_boot_a", null, false)).Code);
            Assert.Empty(BootFlashCheck.Check(Analyze(new byte[4096]), "dtbo", null, false));
            Assert.Empty(BootFlashCheck.Check(Analyze(new byte[4096]), "system_a", null, false));
            Assert.Equal("init_boot", BootFlashCheck.BaseName("init_boot_b"));
        }

        [Fact]
        public void Device_facts_are_parsed_remembered_and_recalled()
        {
            DeviceFacts facts = DeviceFacts.Parse("R5CT", "kernel=5.10.198-android12-9-g1\npatch=2024-09-05\nvendor_patch=\nmodel=SM-S911B\nandroid=14\n",
                new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc));
            Assert.Equal("5.10.198-android12-9-g1", facts.Kernel);
            Assert.Null(facts.VendorPatch);
            Assert.Equal("SM-S911B", facts.Model);

            string stored = DeviceFacts.Remember(null, facts);
            stored = DeviceFacts.Remember(stored, DeviceFacts.Parse("OTHER", "kernel=4.19.1\n", DateTime.UtcNow));
            stored = DeviceFacts.Remember(stored, DeviceFacts.Parse("EMPTY", "", DateTime.UtcNow)); // nothing to keep
            DeviceFacts back = DeviceFacts.Recall(stored, "R5CT");
            Assert.Equal("2024-09-05", back.Patch);
            Assert.Equal(new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc), back.Seen.ToUniversalTime());
            Assert.NotNull(DeviceFacts.Recall(stored, "OTHER"));
            Assert.Null(DeviceFacts.Recall(stored, "EMPTY"));
            Assert.Null(DeviceFacts.Recall("garbage\twithout\tfields", "garbage"));
        }

        // ---------- the Companion module's report ----------

        const string CompanionReport =
            "kernel=5.15.148-android13-8-00017-gabc123\npatch=2026-08-05\nvendor_patch=\nmodel=Infinix X6878\nandroid=14\n" +
            "companion=2.3.0 beta\nroot=KernelSU\navb=orange\ndevice_state=unlocked\nspoofed=true\nselinux=Enforcing\n" +
            "kmi=5.15-android13\nconflicts=4\n";

        [Fact]
        public void The_companion_report_adds_root_avb_and_conflicts()
        {
            DeviceFacts f = DeviceFacts.Parse("X6878", CompanionReport, DateTime.UtcNow);
            Assert.Equal("5.15.148-android13-8-00017-gabc123", f.Kernel);
            Assert.Equal("2.3.0 beta", f.Companion);
            Assert.Equal("KernelSU", f.Root);
            Assert.Equal("orange", f.Avb);
            Assert.Equal("unlocked", f.DeviceState);
            Assert.True(f.Spoofed);
            Assert.Equal(4, f.Conflicts);

            DeviceFacts plain = DeviceFacts.Parse("X6878", "kernel=5.15.1-android13-8\nroot=unknown\nconflicts=many\n", DateTime.UtcNow);
            Assert.Null(plain.Companion);
            Assert.Null(plain.Root);
            Assert.Null(plain.Conflicts);
            Assert.False(plain.Spoofed);
        }

        [Fact]
        public void Companion_fields_are_remembered_and_old_lines_still_load()
        {
            DateTime seen = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
            string stored = DeviceFacts.Remember(null, DeviceFacts.Parse("X6878", CompanionReport, seen));
            DeviceFacts back = DeviceFacts.Recall(stored, "X6878");
            Assert.Equal("2.3.0 beta", back.Companion);
            Assert.Equal("KernelSU", back.Root);
            Assert.Equal("orange", back.Avb);
            Assert.True(back.Spoofed);
            Assert.Equal(4, back.Conflicts);
            Assert.Equal(seen, back.Seen.ToUniversalTime());

            // Written by v2.3.0, before the Companion: seven fields.
            string old = "R5CT\t5.10.198-android12-9-g1\t2024-09-05\t\tSM-S911B\t14\t2026-10-01T00:00:00.0000000Z\n";
            DeviceFacts legacy = DeviceFacts.Recall(old, "R5CT");
            Assert.Equal("SM-S911B", legacy.Model);
            Assert.Null(legacy.Companion);
            Assert.Null(legacy.Conflicts);
        }

        [Fact]
        public void An_image_rooted_with_another_manager_than_the_phone_is_asked_about()
        {
            BootImageAnalysis kernelsuLkm = Analyze(BootImage(4, new byte[0], Gzip(Cpio("init", "init.real", "kernelsu.ko"))));
            DeviceFacts magiskPhone = Phone();
            magiskPhone.Root = "Magisk";
            List<Finding> f = BootFlashCheck.Check(kernelsuLkm, "init_boot_a", magiskPhone, true);
            Finding change = Assert.Single(f, x => x.Code == FindingCode.RootManagerChange);
            Assert.Equal(FindingLevel.Warn, change.Level);
            Assert.Equal(new[] { "KernelSU", "Magisk" }, change.Values);

            DeviceFacts kernelsuPhone = Phone();
            kernelsuPhone.Root = "KernelSU";
            Assert.DoesNotContain(BootFlashCheck.Check(kernelsuLkm, "init_boot_a", kernelsuPhone, true), x => x.Code == FindingCode.RootManagerChange);
            // Without the Companion the phone's root manager is not known, so nothing is said.
            Assert.DoesNotContain(BootFlashCheck.Check(kernelsuLkm, "init_boot_a", Phone(), true), x => x.Code == FindingCode.RootManagerChange);
            Assert.Equal(RootKind.APatch, BootFlashCheck.RootFromName(" apatch "));
            Assert.Equal(RootKind.None, BootFlashCheck.RootFromName(null));
        }
    }
}
