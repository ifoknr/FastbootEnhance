using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>One line of "adb devices -l".</summary>
    public sealed class AdbDevice
    {
        public string Serial { get; private set; }

        /// <summary>device, recovery, sideload, rescue, unauthorized, offline, ...</summary>
        public string State { get; private set; }

        public string Model { get; private set; }
        public string Product { get; private set; }

        /// <summary>True when commands can be run: a booted system or a recovery with adb.</summary>
        public bool Usable => State == "device" || State == "recovery";

        public bool Unauthorized => State == "unauthorized";

        /// <summary>
        /// Parses "adb devices -l". Lines that are not devices (the header, "* daemon started"
        /// notices, blank lines) are skipped.
        /// </summary>
        public static List<AdbDevice> ParseList(string output)
        {
            List<AdbDevice> devices = new List<AdbDevice>();
            if (string.IsNullOrEmpty(output))
                return devices;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("*") || line.StartsWith("List of devices", StringComparison.Ordinal))
                    continue;

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                AdbDevice device = new AdbDevice { Serial = parts[0], State = parts[1] };
                int next = 2;

                // Linux prints "no permissions (...)" as the state; keep it as one word.
                if (parts[1] == "no" && parts.Length > 2 && parts[2].StartsWith("permissions", StringComparison.Ordinal))
                {
                    device.State = "no permissions";
                    next = 3;
                }

                for (int i = next; i < parts.Length; i++)
                {
                    int colon = parts[i].IndexOf(':');
                    if (colon <= 0)
                        continue;
                    string key = parts[i].Substring(0, colon);
                    string value = parts[i].Substring(colon + 1);
                    if (key == "model")
                        device.Model = value.Replace('_', ' ');
                    else if (key == "product")
                        device.Product = value;
                }

                devices.Add(device);
            }

            return devices;
        }
    }
}
