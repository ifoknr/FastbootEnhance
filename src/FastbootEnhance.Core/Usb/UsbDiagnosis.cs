using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FastbootEnhance.Core.Usb
{
    /// <summary>One USB device as Windows lists it (Win32_PnPEntity).</summary>
    public sealed class PnpDevice
    {
        public PnpDevice(string name, string deviceId, int errorCode, string pnpClass = null)
        {
            Name = name ?? "";
            DeviceId = deviceId ?? "";
            ErrorCode = errorCode;
            PnpClass = pnpClass ?? "";
        }

        public string Name { get; }

        /// <summary>"USB\VID_18D1&amp;PID_4EE0\SERIAL".</summary>
        public string DeviceId { get; }

        /// <summary>ConfigManagerErrorCode: 0 works, 28 has no driver, others are driver problems.</summary>
        public int ErrorCode { get; }

        public string PnpClass { get; }
    }

    public enum UsbFindingKind
    {
        /// <summary>A phone Windows sees but has no driver for (code 28).</summary>
        MissingDriver,

        /// <summary>A phone whose driver is installed but not working.</summary>
        DriverProblem,

        /// <summary>Qualcomm emergency download (EDL, 9008): QFIL talks to it, fastboot cannot.</summary>
        QualcommEdl,

        /// <summary>MediaTek boot ROM or preloader: SP Flash Tool mode.</summary>
        MediaTekDownload,

        /// <summary>Unisoc (Spreadtrum) download mode.</summary>
        UnisocDownload,

        /// <summary>Samsung Download (Odin) mode; Samsung phones have no fastboot.</summary>
        SamsungDownload,

        /// <summary>A fastboot interface with a working driver.</summary>
        FastbootReady,

        /// <summary>An adb interface with a working driver: the phone is in Android or recovery.</summary>
        AdbReady,
    }

    public sealed class UsbFinding
    {
        internal UsbFinding(UsbFindingKind kind, PnpDevice device, string vid, string pid)
        {
            Kind = kind;
            Device = device;
            Vid = vid;
            Pid = pid;
        }

        public UsbFindingKind Kind { get; }
        public PnpDevice Device { get; }

        /// <summary>Vendor and product id, four upper-case hex digits; null when not in the id.</summary>
        public string Vid { get; }
        public string Pid { get; }

        public string UsbId => Vid != null ? Vid + ":" + Pid : null;
    }

    /// <summary>
    /// Why a phone plugged in by USB may not show up in fastboot: no driver, a broken driver,
    /// or a mode fastboot cannot reach (EDL, MediaTek BROM, Odin...).
    /// </summary>
    public static class UsbDiagnosis
    {
        static readonly Regex UsbIdPattern = new Regex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Vendors of Android phones and their chips. A device of one of them with a driver
        // problem is very likely the phone.
        static readonly HashSet<string> PhoneVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "18D1", // Google (the generic fastboot and adb ids)
            "0E8D", // MediaTek
            "05C6", // Qualcomm
            "1782", // Unisoc / Spreadtrum
            "2717", // Xiaomi
            "04E8", // Samsung
            "12D1", // Huawei
            "2A70", // OnePlus
            "22D9", // OPPO / realme
            "2D95", // vivo
            "22B8", // Motorola
            "17EF", // Lenovo
            "0BB4", // HTC
            "1004", // LG
            "0FCE", // Sony
            "19D2", // ZTE
            "1BBB", // TCL / Alcatel
            "2A45", // Meizu
            "2E04", // HMD (Nokia)
            "2AE5", // Fairphone
        };

        static readonly string[] PhoneWords = { "android", "adb", "fastboot", "bootloader", "mtp" };

        public static IReadOnlyList<UsbFinding> Diagnose(IEnumerable<PnpDevice> devices)
        {
            List<UsbFinding> findings = new List<UsbFinding>();
            foreach (PnpDevice device in devices)
            {
                string vid = null, pid = null;
                Match match = UsbIdPattern.Match(device.DeviceId);
                if (match.Success)
                {
                    vid = match.Groups[1].Value.ToUpperInvariant();
                    pid = match.Groups[2].Value.ToUpperInvariant();
                }

                UsbFindingKind? kind = Classify(device, vid, pid);
                if (kind != null)
                    findings.Add(new UsbFinding(kind.Value, device, vid, pid));
            }

            // Problems first, then special modes, then what works.
            findings.Sort((x, y) => x.Kind.CompareTo(y.Kind));
            return findings;
        }

        static UsbFindingKind? Classify(PnpDevice device, string vid, string pid)
        {
            // Download modes are recognised by their ids, with or without a driver.
            if (vid == "05C6" && pid == "9008")
                return UsbFindingKind.QualcommEdl;
            if (vid == "0E8D" && (pid == "0003" || pid == "2000" || pid == "2001"))
                return UsbFindingKind.MediaTekDownload;
            if (vid == "1782" && pid == "4D00")
                return UsbFindingKind.UnisocDownload;
            if (vid == "04E8" && pid == "685D")
                return UsbFindingKind.SamsungDownload;

            string name = device.Name.ToLowerInvariant();
            bool looksLikePhone = (vid != null && PhoneVendors.Contains(vid)) || ContainsAny(name, PhoneWords);

            if (device.ErrorCode != 0)
            {
                if (!looksLikePhone)
                    return null;
                return device.ErrorCode == 28 ? UsbFindingKind.MissingDriver : UsbFindingKind.DriverProblem;
            }

            // Working interfaces, named by the Google driver and by most phone makers' drivers.
            if (name.Contains("bootloader interface") || name.Contains("fastboot"))
                return UsbFindingKind.FastbootReady;
            if (name.Contains("adb interface") || name.Contains("adb device"))
                return UsbFindingKind.AdbReady;
            return null;
        }

        static bool ContainsAny(string text, string[] words)
        {
            foreach (string word in words)
            {
                if (text.Contains(word))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reads devices from lines of "name|device id|error code|class", as the app's test
        /// stand-in writes them. Malformed lines are skipped.
        /// </summary>
        public static IReadOnlyList<PnpDevice> ParseList(string text)
        {
            List<PnpDevice> devices = new List<PnpDevice>();
            foreach (string raw in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = raw.TrimEnd('\r').Split('|');
                int code;
                if (fields.Length < 3 || !int.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    continue;
                devices.Add(new PnpDevice(fields[0].Trim(), fields[1].Trim(), code, fields.Length > 3 ? fields[3].Trim() : null));
            }
            return devices;
        }
    }
}
