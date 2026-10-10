using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FastbootEnhance.Core.Adb;

namespace FastbootEnhance
{
    /// <summary>
    /// Asks a phone in Android about itself over adb and remembers the answer per serial, so the
    /// device page can show it and check boot images against it once the phone is in the
    /// bootloader. The kernel and patch level need no root. With the Fastboot Studio Companion
    /// module installed, su gives more: the root manager, the AVB state and module conflicts.
    /// </summary>
    static class AdbFacts
    {
        // Long enough to tap "Allow" when the root manager asks; a refusal comes back at once.
        static readonly TimeSpan CompanionTimeout = TimeSpan.FromSeconds(20);

        static readonly HashSet<string> askedThisSession = new HashSet<string>();
        static readonly object gate = new object();

        /// <summary>Raised (on a worker thread) after a phone's facts were stored.</summary>
        public static event Action<DeviceFacts> Remembered;

        public static DeviceFacts Recall(string serial)
        {
            return DeviceFacts.Recall(Settings.Get(FastbootUI.DeviceFactsKey), serial);
        }

        /// <summary>Reads the phone once per run of the app, in the background.</summary>
        public static void ReadOnce(string serial)
        {
            lock (gate)
            {
                if (serial == null || !askedThisSession.Add(serial))
                    return;
            }
            ThreadPool.QueueUserWorkItem(delegate { Read(serial); });
        }

        /// <summary>Reads every phone booted into Android that has not been read in this run yet. Blocks.</summary>
        public static void ReadConnected()
        {
            Adb.Result list = Adb.Run("devices -l", Adb.ShortCommand);
            if (!list.Succeeded)
                return;
            foreach (AdbDevice device in AdbDevice.ParseList(list.Output).Where(d => d.State == "device"))
            {
                bool first;
                lock (gate)
                    first = askedThisSession.Add(device.Serial);
                if (first)
                    Read(device.Serial);
            }
        }

        /// <summary>
        /// Asks the phone and stores what it said. Blocks, so call it on a worker thread. A phone
        /// that does not answer is simply not remembered.
        /// </summary>
        public static DeviceFacts Read(string serial)
        {
            try
            {
                Adb.Result basic = Adb.Run(AdbCommand.Shell(serial, DeviceFacts.Command), Adb.ShortCommand);
                DeviceFacts facts = basic.Succeeded ? DeviceFacts.Parse(serial, basic.Output, DateTime.UtcNow) : null;
                // Kept straight away: if the phone restarts to the bootloader while su waits for
                // an answer, the kernel is still known.
                if (facts != null && !facts.Empty)
                    Settings.Set(FastbootUI.DeviceFactsKey, DeviceFacts.Remember(Settings.Get(FastbootUI.DeviceFactsKey), facts));

                // The Companion lives under /data/adb, which only root can reach. Ask su only when
                // the phone has one; Magisk shows a prompt the first time, KernelSU and APatch
                // answer from their own allow list.
                Adb.Result su = Adb.Run(AdbCommand.Shell(serial, "command -v su"), Adb.ShortCommand);
                if (su.Succeeded && su.Output.Trim().Length > 0)
                {
                    LogStore.Append("ADB device " + serial + ": asking for root to read Fastboot Studio Companion "
                        + "(allow Shell in the root manager, or ignore this)");
                    Adb.Result report = Adb.Run(AdbCommand.Shell(serial,
                        AdbCommand.AsRoot(DeviceFacts.CompanionCommand, RootAccess.Su)), CompanionTimeout);
                    if (report.Succeeded)
                    {
                        DeviceFacts full = DeviceFacts.Parse(serial, report.Output, DateTime.UtcNow);
                        if (full.Companion != null && !full.Empty)
                            facts = full;
                    }
                }

                if (facts == null || facts.Empty)
                    return null;
                Settings.Set(FastbootUI.DeviceFactsKey, DeviceFacts.Remember(Settings.Get(FastbootUI.DeviceFactsKey), facts));
                LogStore.Append("ADB device " + serial + ": kernel " + (facts.Kernel ?? "?") + ", security patch " + (facts.Patch ?? "?")
                    + (facts.Companion != null
                        ? ", Companion " + facts.Companion + " (" + (facts.Root ?? "root ?") + ", AVB " + (facts.Avb ?? "?")
                          + ", " + (facts.Conflicts ?? 0) + " module conflicts)"
                        : ""));
                Remembered?.Invoke(facts);
                return facts;
            }
            catch (FileNotFoundException)
            {
                return null;   // no adb.exe: the device page already says so
            }
            catch (Exception e)
            {
                LogStore.Append("ADB device " + serial + ": not read: " + e.Message);
                return null;
            }
        }
    }
}
