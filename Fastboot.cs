using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FastbootEnhance
{
    class Fastboot : IDisposable
    {
        /// <summary>Enough for devices, getvar, reboot and the other short commands.</summary>
        public static readonly TimeSpan ShortCommand = TimeSpan.FromSeconds(60);

        /// <summary>For reboots and other control commands that can wait on a slow device.</summary>
        public static readonly TimeSpan LongCommand = TimeSpan.FromMinutes(15);

        /// <summary>
        /// For commands that write to the device. A fixed limit could kill fastboot halfway
        /// through a large image on slow USB and leave the partition partly written, which is
        /// worse than a command that takes its time.
        /// </summary>
        public static readonly TimeSpan NoLimit = Timeout.InfiniteTimeSpan;

        /// <summary>
        /// Resolved once against the directory the app was installed into. The old code used
        /// ".\\fastboot.exe", which is relative to the *working* directory and breaks whenever
        /// the app is started from a shortcut or another folder.
        /// </summary>
        static readonly string ExecutablePath =
            Path.Combine(AppContext.BaseDirectory, "fastboot.exe");

        Process process;
        Timer deadline;
        volatile bool timedOut;

        public StreamReader stdout;
        public StreamReader stderr;

        public Fastboot(string serial, string action) : this(serial, action, ShortCommand)
        {
        }

        /// <summary>
        /// Starts fastboot and arms a deadline. When it passes, the process is killed, which
        /// closes its pipes, so a caller blocked reading stdout or stderr is released instead
        /// of waiting forever on a hung device.
        /// </summary>
        public Fastboot(string serial, string action, TimeSpan timeout)
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

            Process started = process;
            deadline = new Timer(delegate
            {
                timedOut = true;
                KillQuietly(started);
            }, null, timeout, Timeout.InfiniteTimeSpan);
        }

        /// <summary>True when the deadline passed and the process was killed.</summary>
        public bool TimedOut => timedOut;

        /// <summary>
        /// Waits for fastboot to exit after its output has been read, and returns the exit code,
        /// or null when the deadline killed it. The deadline guarantees this returns.
        /// </summary>
        public int? WaitForExit()
        {
            Process current = process;
            if (current == null)
                return null;

            current.WaitForExit();
            if (timedOut)
                return null;
            return current.ExitCode;
        }

        /// <summary>
        /// Stops fastboot from another thread (the app is closing) and waits briefly for it to
        /// let go of the image it was sending.
        /// </summary>
        public void Abort()
        {
            Process current = process;
            if (current == null)
                return;
            try
            {
                KillQuietly(current);
                current.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Disposed by its owner in the meantime; it is already gone.
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        static void KillQuietly(Process target)
        {
            try
            {
                if (target != null && !target.HasExited)
                    target.Kill();
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
            Timer timer = deadline;
            deadline = null;
            if (timer != null)
                timer.Dispose();

            Process current = process;
            process = null;
            if (current == null)
                return;

            // Close() on its own leaves a hung fastboot running and holding the USB device.
            KillQuietly(current);
            current.Dispose();
            stdout = null;
            stderr = null;
        }
    }
}
