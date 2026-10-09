using FastbootEnhance.Core.Usb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace FastbootEnhance
{
    /// <summary>The USB devices Windows knows about, for the USB check.</summary>
    static class UsbScan
    {
        /// <summary>
        /// A file of "name|device id|error code|class" lines read instead of Windows' list;
        /// used by CI, which has no phone to plug in.
        /// </summary>
        const string StandInVariable = "FASTBOOT_STUDIO_FAKE_USB";

        public static IReadOnlyList<PnpDevice> Read()
        {
            string standIn = Environment.GetEnvironmentVariable(StandInVariable);
            if (!string.IsNullOrEmpty(standIn) && File.Exists(standIn))
                return UsbDiagnosis.ParseList(File.ReadAllText(standIn));

            List<PnpDevice> devices = new List<PnpDevice>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject entry in results)
                {
                    using (entry)
                    {
                        object code = entry["ConfigManagerErrorCode"];
                        devices.Add(new PnpDevice(
                            entry["Name"] as string,
                            entry["PNPDeviceID"] as string,
                            code != null ? Convert.ToInt32(code) : 0,
                            entry["PNPClass"] as string));
                    }
                }
            }
            return devices;
        }
    }
}
