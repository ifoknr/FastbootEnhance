using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastbootEnhance
{
    /// <summary>
    /// One adb.exe process. Standard output is left as a raw stream so exec-out can carry a
    /// partition image byte for byte; standard error is collected on the side so a chatty
    /// adb can never block on a full pipe.
    /// </summary>
    sealed class Adb : IDisposable
    {
        public static readonly TimeSpan ShortCommand = TimeSpan.FromSeconds(30);

        /// <summary>Long enough to allow a root prompt on the phone to be answered.</summary>
        public static readonly TimeSpan RootPrompt = TimeSpan.FromSeconds(45);

        static readonly string ExecutablePath = Path.Combine(AppContext.BaseDirectory, "adb.exe");

        /// <summary>Set once this app has started adb, so the server is stopped again on exit.</summary>
        public static volatile bool StartedServer;

        Process process;
        Timer deadline;
        readonly Task<string> errors;
        volatile bool timedOut;

        public Adb(string arguments, TimeSpan timeout)
        {
            if (!File.Exists(ExecutablePath))
                throw new FileNotFoundException("adb.exe is missing", ExecutablePath);

            process = new Process();
            process.StartInfo.FileName = ExecutablePath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = AppContext.BaseDirectory;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.Start();
            StartedServer = true;

            // Nothing is ever typed into adb; closing stdin keeps "shell" from waiting on it.
            process.StandardInput.Close();
            errors = process.StandardError.ReadToEndAsync();

            Process started = process;
            deadline = new Timer(delegate
            {
                timedOut = true;
                KillQuietly(started);
            }, null, timeout, Timeout.InfiniteTimeSpan);
        }

        /// <summary>The raw output, for binary transfers.</summary>
        public Stream Output => process.StandardOutput.BaseStream;

        public string ReadOutput()
        {
            return process.StandardOutput.ReadToEnd();
        }

        public bool TimedOut => timedOut;

        /// <summary>Waits for adb to exit; null when it was stopped by its deadline or by Abort.</summary>
        public int? WaitForExit()
        {
            Process current = process;
            if (current == null)
                return null;
            current.WaitForExit();
            return timedOut ? (int?)null : current.ExitCode;
        }

        /// <summary>What adb wrote to standard error, once it has exited.</summary>
        public string Errors
        {
            get
            {
                try
                {
                    return errors.Wait(TimeSpan.FromSeconds(5)) ? errors.Result.Trim() : "";
                }
                catch (AggregateException)
                {
                    return "";
                }
            }
        }

        /// <summary>Stops adb from another thread, for Cancel or when the app closes.</summary>
        public void Abort()
        {
            timedOut = true;
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
            KillQuietly(current);
            current.Dispose();
        }

        public sealed class Result
        {
            public int? ExitCode;
            public string Output;
            public string Errors;

            public bool Succeeded => ExitCode == 0;

            /// <summary>The most useful line to show when the command failed.</summary>
            public string Problem
            {
                get
                {
                    if (ExitCode == null)
                        return "adb did not answer in time";
                    string text = (Errors ?? "").Length > 0 ? Errors : (Output ?? "");
                    string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return lines.Length > 0 ? lines[lines.Length - 1].Trim() : "adb exited with code " + ExitCode;
                }
            }
        }

        /// <summary>Runs a short command and returns its text output.</summary>
        public static Result Run(string arguments, TimeSpan timeout)
        {
            using (Adb adb = new Adb(arguments, timeout))
            {
                string output = adb.ReadOutput();
                int? exit = adb.WaitForExit();
                return new Result { ExitCode = exit, Output = output, Errors = adb.Errors };
            }
        }

        /// <summary>Stops the adb server this app started, so it does not hold the app's folder.</summary>
        public static void StopServer()
        {
            if (!StartedServer)
                return;
            try
            {
                Run("kill-server", TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Best effort on the way out.
            }
        }
    }
}
