using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>
    /// What a phone said about itself the last time it was seen over adb: its kernel and
    /// patch level. fastboot cannot ask these, so the app keeps them per serial number to check
    /// a boot image against the phone later, when the phone is in the bootloader.
    /// </summary>
    public sealed class DeviceFacts
    {
        /// <summary>One adb shell command that prints every fact as key=value.</summary>
        public const string Command =
            "echo kernel=$(uname -r); " +
            "echo patch=$(getprop ro.build.version.security_patch); " +
            "echo vendor_patch=$(getprop ro.vendor.build.security_patch); " +
            "echo model=$(getprop ro.product.model); " +
            "echo android=$(getprop ro.build.version.release)";

        public string Serial { get; set; }
        public string Kernel { get; set; }
        public string Patch { get; set; }
        public string VendorPatch { get; set; }
        public string Model { get; set; }
        public string Android { get; set; }
        public DateTime Seen { get; set; }

        public static DeviceFacts Parse(string serial, string output, DateTime seen)
        {
            DeviceFacts facts = new DeviceFacts { Serial = serial, Seen = seen };
            foreach (string raw in (output ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0)
                    continue;
                switch (line.Substring(0, eq))
                {
                    case "kernel": facts.Kernel = value; break;
                    case "patch": facts.Patch = value; break;
                    case "vendor_patch": facts.VendorPatch = value; break;
                    case "model": facts.Model = value; break;
                    case "android": facts.Android = value; break;
                }
            }
            return facts;
        }

        public bool Empty => Kernel == null && Patch == null;

        /// <summary>
        /// The remembered phones as text, one line per phone (tab-separated fields), newest
        /// first; at most 20 are kept.
        /// </summary>
        public static string Remember(string stored, DeviceFacts facts)
        {
            List<DeviceFacts> all = Load(stored);
            all.RemoveAll(f => f.Serial == facts?.Serial);
            if (!string.IsNullOrEmpty(facts?.Serial) && !facts.Empty)
                all.Add(facts);
            all.Sort((x, y) => y.Seen.CompareTo(x.Seen));
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < all.Count && i < 20; i++)
            {
                DeviceFacts f = all[i];
                text.Append(string.Join("\t", Clean(f.Serial), Clean(f.Kernel), Clean(f.Patch), Clean(f.VendorPatch),
                    Clean(f.Model), Clean(f.Android), f.Seen.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))).Append('\n');
            }
            return text.ToString();
        }

        public static DeviceFacts Recall(string stored, string serial)
        {
            return serial == null ? null : Load(stored).Find(f => f.Serial == serial);
        }

        static List<DeviceFacts> Load(string stored)
        {
            List<DeviceFacts> all = new List<DeviceFacts>();
            foreach (string line in (stored ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 7 || p[0].Length == 0)
                    continue;
                DateTime seen;
                DateTime.TryParse(p[6], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out seen);
                all.Add(new DeviceFacts
                {
                    Serial = p[0], Kernel = Null(p[1]), Patch = Null(p[2]), VendorPatch = Null(p[3]),
                    Model = Null(p[4]), Android = Null(p[5]), Seen = seen,
                });
            }
            return all;
        }

        static string Clean(string value) => (value ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        static string Null(string value) => value.Length == 0 ? null : value;
    }
}
