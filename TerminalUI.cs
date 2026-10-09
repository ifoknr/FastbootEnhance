using FastbootEnhance.Core.Terminal;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastbootEnhance
{
    /// <summary>
    /// The Terminal page: runs adb and fastboot commands typed by the user, with the bundled
    /// tools, in a folder of their choice. Commands that can brick the phone or wipe it ask
    /// first, the same way the rest of the app does.
    /// </summary>
    static class TerminalUI
    {
        static MainWindow W => MainWindow.THIS;

        /// <summary>Older output is dropped beyond this, so a long logcat cannot fill memory.</summary>
        const int MaxOutputChars = 400000;

        static readonly string[] QuickCommands =
        {
            "fastboot devices",
            "fastboot getvar all",
            "fastboot reboot",
            "fastboot reboot bootloader",
            "adb devices -l",
            "adb reboot bootloader",
            "adb shell getprop ro.product.model",
        };

        static readonly List<string> history = new List<string>();
        static int historyIndex;
        static string folder;
        static volatile Process running;

        // Output arrives line by line from other threads and is shown in batches, so a chatty
        // command (logcat) does not flood the window with one update per line.
        static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();
        static DispatcherTimer flusher;

        public static void init()
        {
            folder = Settings.Get("terminal_folder");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloads = Path.Combine(profile, "Downloads");
                folder = Directory.Exists(downloads) ? downloads : profile;
            }
            showFolder();

            foreach (string quick in QuickCommands)
            {
                string text = quick;
                System.Windows.Controls.Button chip = new System.Windows.Controls.Button
                {
                    Content = text,
                    FontFamily = (System.Windows.Media.FontFamily)W.FindResource("Mono"),
                    FontSize = 12,
                    MinHeight = 30,
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(0, 0, 8, 8),
                    Style = (Style)W.FindResource("GhostButton"),
                };
                // Fills the line rather than running it, so nothing happens by a stray click.
                chip.Click += delegate
                {
                    W.terminal_input.Text = text;
                    W.terminal_input.Focus();
                    W.terminal_input.CaretIndex = text.Length;
                };
                W.terminal_quick.Children.Add(chip);
            }

            W.terminal_run.Click += delegate { submit(); };
            W.terminal_stop.Click += delegate { stop(); };
            W.terminal_clear.Click += delegate { W.terminal_output.Clear(); };
            W.terminal_folder.Click += delegate { chooseFolder(); };
            W.terminal_input.PreviewKeyDown += onKey;

            // A file dropped on the line is added as a quoted path, ready for "flash boot".
            W.terminal_input.AllowDrop = true;
            W.terminal_input.PreviewDragOver += delegate (object sender, DragEventArgs e)
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            W.terminal_input.PreviewDrop += delegate (object sender, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0)
                    return;
                e.Handled = true;
                string line = W.terminal_input.Text.TrimEnd();
                W.terminal_input.Text = (line.Length > 0 ? line + " " : "") + "\"" + files[0] + "\"";
                W.terminal_input.CaretIndex = W.terminal_input.Text.Length;
                W.terminal_input.Focus();
            };

            flusher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            flusher.Tick += delegate { flush(); };
            flusher.Start();

            write(Properties.Resources.terminal_welcome);
        }

        /// <summary>Stops a command still running when the app closes.</summary>
        public static void shutdown()
        {
            stop();
        }

        static void onKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    submit();
                    break;
                case Key.Up:
                    if (history.Count == 0)
                        break;
                    e.Handled = true;
                    historyIndex = Math.Max(0, historyIndex - 1);
                    W.terminal_input.Text = history[historyIndex];
                    W.terminal_input.CaretIndex = W.terminal_input.Text.Length;
                    break;
                case Key.Down:
                    if (history.Count == 0)
                        break;
                    e.Handled = true;
                    historyIndex = Math.Min(history.Count, historyIndex + 1);
                    W.terminal_input.Text = historyIndex < history.Count ? history[historyIndex] : "";
                    W.terminal_input.CaretIndex = W.terminal_input.Text.Length;
                    break;
            }
        }

        static void submit()
        {
            string line = W.terminal_input.Text.Trim();
            if (line.Length == 0 || running != null)
                return;

            if (history.Count == 0 || history[history.Count - 1] != line)
                history.Add(line);
            historyIndex = history.Count;
            W.terminal_input.Clear();
            write("> " + line);

            string lower = line.ToLowerInvariant();
            if (lower == "clear" || lower == "cls")
            {
                W.terminal_output.Clear();
                return;
            }
            if (lower == "help")
            {
                write(Properties.Resources.terminal_help);
                return;
            }
            if (lower == "cd" || lower.StartsWith("cd ", StringComparison.Ordinal))
            {
                changeFolder(line.Substring(2).Trim().Trim('"'));
                return;
            }

            TerminalCommand command = TerminalCommand.Parse(line);
            switch (command.Problem)
            {
                case TerminalProblem.NotAdbOrFastboot:
                    write(Properties.Resources.terminal_not_tool);
                    return;
                case TerminalProblem.InteractiveShell:
                    write(Properties.Resources.terminal_interactive);
                    return;
                case TerminalProblem.UnclosedQuote:
                    write(Properties.Resources.terminal_quote);
                    return;
                case TerminalProblem.Empty:
                    return;
            }

            // A second fastboot talking to the phone mid-flash could break the flash.
            if (FastbootUI.flashing)
            {
                write(Properties.Resources.terminal_busy);
                return;
            }

            if (!confirm(command))
            {
                write(Properties.Resources.terminal_cancelled);
                return;
            }

            start(command, line);
        }

        static bool confirm(TerminalCommand command)
        {
            switch (command.Risk)
            {
                case TerminalRisk.CriticalPartition:
                    return ThemedDialog.ConfirmTyped(string.Format(Properties.Resources.terminal_critical, command.RiskTarget),
                        Properties.Resources.critical_title, command.RiskTarget);
                case TerminalRisk.LocksBootloader:
                    return ThemedDialog.ConfirmTyped(Properties.Resources.terminal_lock, Properties.Resources.critical_title, "lock");
                case TerminalRisk.RawBlockWrite:
                    return ThemedDialog.ConfirmTyped(Properties.Resources.terminal_dd, Properties.Resources.critical_title, "dd");
                case TerminalRisk.WipesData:
                    return Helper.confirm(Properties.Resources.terminal_wipe, Properties.Resources.confirm_title, true);
                default:
                    return true;
            }
        }

        static void start(TerminalCommand command, string line)
        {
            string exe = Path.Combine(AppContext.BaseDirectory, command.Tool == TerminalTool.Adb ? "adb.exe" : "fastboot.exe");
            Process process = new Process();
            process.StartInfo.FileName = exe;
            process.StartInfo.Arguments = command.Arguments;
            process.StartInfo.WorkingDirectory = folder;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) pending.Enqueue(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) pending.Enqueue(e.Data); };

            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                write(string.Format(Properties.Resources.terminal_start_failed, e.Message));
                process.Dispose();
                return;
            }

            if (command.Tool == TerminalTool.Adb)
                Adb.StartedServer = true;
            LogStore.Append("terminal> " + line);
            running = process;
            setRunning(true);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Nothing is typed into the tools; a closed input keeps them from waiting on it.
            process.StandardInput.Close();

            Thread waiter = new Thread(new ThreadStart(delegate
            {
                int? code = null;
                try
                {
                    // Without a timeout this also waits for the last output lines to arrive.
                    process.WaitForExit();
                    code = process.ExitCode;
                }
                catch (Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }

                W.Dispatcher.BeginInvoke(new Action(delegate
                {
                    flush();
                    running = null;
                    setRunning(false);
                    write(string.Format(Properties.Resources.terminal_exit, code?.ToString() ?? "?"));
                    LogStore.Append("terminal: exit " + (code?.ToString() ?? "?"));
                }));
            }));
            waiter.IsBackground = true;
            waiter.Start();
        }

        static void stop()
        {
            Process current = running;
            if (current == null)
                return;
            try
            {
                current.Kill(true);
                pending.Enqueue(Properties.Resources.terminal_stopped);
            }
            catch (Exception)
            {
            }
        }

        static void setRunning(bool busy)
        {
            W.terminal_run.IsEnabled = !busy;
            W.terminal_stop.IsEnabled = busy;
        }

        static void changeFolder(string target)
        {
            if (target.Length == 0)
            {
                write(folder);
                return;
            }
            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(folder, Environment.ExpandEnvironmentVariables(target)));
            }
            catch (Exception)
            {
                path = null;
            }
            if (path == null || !Directory.Exists(path))
            {
                write(string.Format(Properties.Resources.terminal_no_folder, target));
                return;
            }
            setFolder(path);
            write(folder);
        }

        static void chooseFolder()
        {
            Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = folder };
            if (dialog.ShowDialog() == true)
                setFolder(dialog.FolderName);
        }

        static void setFolder(string path)
        {
            folder = path;
            Settings.Set("terminal_folder", path);
            showFolder();
        }

        static void showFolder()
        {
            W.terminal_folder_text.Text = string.Format(Properties.Resources.terminal_folder_now, folder);
        }

        static void write(string text)
        {
            pending.Enqueue(text);
            flush();
        }

        static void flush()
        {
            if (pending.IsEmpty)
                return;
            StringBuilder batch = new StringBuilder();
            string line;
            while (pending.TryDequeue(out line))
                batch.Append(line).Append('\n');

            System.Windows.Controls.TextBox output = W.terminal_output;
            output.AppendText(batch.ToString());
            if (output.Text.Length > MaxOutputChars)
                output.Text = output.Text.Substring(output.Text.Length - MaxOutputChars / 2);
            output.ScrollToEnd();
        }
    }
}
