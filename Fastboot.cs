using System;
using System.Diagnostics;
using System.IO;

namespace FastbootEnhance
{
    class Fastboot : IDisposable
    {
        /// <summary>
        /// Resolved once against the directory the app was installed into. The old code used
        /// ".\\fastboot.exe", which is relative to the *working* directory and breaks whenever
        /// the app is started from a shortcut or another folder.
        /// </summary>
        static readonly string ExecutablePath =
            Path.Combine(AppContext.BaseDirectory, "fastboot.exe");

        Process process;
        public StreamReader stdout;
        public StreamReader stderr;

        public Fastboot(string serial, string action)
        {
            if (!File.Exists(ExecutablePath))
                throw new FileNotFoundException("fastboot.exe is missing", ExecutablePath);

            process = new Process();
            process.StartInfo.FileName = ExecutablePath;
            process.StartInfo.Arguments = serial == null
                ? action
                : "-s \"" + serial + "\" " + action;
            process.StartInfo.WorkingDirectory = AppContext.BaseDirectory;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            stdout = process.StandardOutput;
            stderr = process.StandardError;
        }

        /// <summary>
        /// Waits for fastboot to finish and returns its exit code, or null if it had to be
        /// stopped after <paramref name="timeout"/>.
        /// </summary>
        public int? WaitForExit(TimeSpan timeout)
        {
            if (process == null)
                return null;

            if (process.WaitForExit((int)timeout.TotalMilliseconds))
                return process.ExitCode;

            KillQuietly();
            return null;
        }

        void KillQuietly()
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            if (process == null)
                return;

            // Close() on its own leaves a hung fastboot running and holding the USB device.
            KillQuietly();
            process.Dispose();
            process = null;
            stdout = null;
            stderr = null;
        }
    }
}
