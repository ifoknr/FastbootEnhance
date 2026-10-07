using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastbootEnhance.Core;
using FastbootEnhance.Core.Adb;

namespace FastbootEnhance
{
    /// <summary>
    /// The Backup page: copies partitions (with root or a custom recovery) and any file or
    /// folder from the phone to this computer, over adb. Every adb call runs on a worker
    /// thread; the page is updated through the dispatcher.
    /// </summary>
    static class BackupUI
    {
        static MainWindow W => MainWindow.THIS;

        static List<AdbDevice> devices = new List<AdbDevice>();
        static AdbDevice current;

        static RootAccess root = RootAccess.None;
        static string partitionDirectory;
        static readonly ObservableCollection<BackupRow> partitionRows = new ObservableCollection<BackupRow>();
        static readonly ObservableCollection<FileRow> fileRows = new ObservableCollection<FileRow>();
        static string currentFolder = "/sdcard";

        /// <summary>Where backups and copied files go: Documents\Fastboot Studio by default.</summary>
        static string saveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fastboot Studio");

        static string lastOutputFolder;

        static volatile bool busy;
        static volatile bool pageVisible;
        static CancellationTokenSource cancel;
        static volatile Adb activeAdb;
        static volatile Thread worker;

        static void log(string line)
        {
            LogStore.Append(line);
        }

        static void ui(Action action)
        {
            W.Dispatcher.BeginInvoke(action);
        }

        public static void init()
        {
            W.backup_partition_list.ItemsSource = partitionRows;
            W.files_list.ItemsSource = fileRows;
            updateSaveRootText();
            setBusy(false);
            showDevice();

            W.main_tabs.SelectionChanged += delegate (object sender, SelectionChangedEventArgs e)
            {
                if (e.OriginalSource != W.main_tabs)
                    return;
                pageVisible = W.main_tabs.SelectedItem == W.backup_tab;
            };

            Thread poller = new Thread(pollDevices);
            poller.IsBackground = true;
            poller.Start();

            W.backup_devices.SelectionChanged += delegate
            {
                AdbDevice picked = W.backup_devices.SelectedItem as AdbDevice;
                if (picked != null && (current == null || picked.Serial != current.Serial))
                    selectDevice(picked);
            };

            // Partitions
            W.backup_read.Click += delegate { readPartitionTable(); };
            W.backup_select_critical.Click += delegate
            {
                foreach (BackupRow row in partitionRows)
                    row.Selected = row.Partition.IsCritical;
            };
            W.backup_select_none.Click += delegate
            {
                foreach (BackupRow row in partitionRows)
                    row.Selected = false;
            };
            W.backup_filter.TextChanged += delegate { applyFilter(); };
            W.backup_start.Click += delegate { startPartitionBackup(); };
            W.backup_cancel.Click += delegate { cancelWork(); };
            W.backup_verify.Click += delegate { verifyBackup(); };

            // Files
            W.files_go.Click += delegate { openFolder(W.files_path.Text.Trim()); };
            W.files_path.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                openFolder(W.files_path.Text.Trim());
            };
            W.files_up.Click += delegate { openFolder(DeviceEntry.Parent(currentFolder)); };
            foreach (Button quick in new[] { W.files_quick_storage, W.files_quick_dcim, W.files_quick_download,
                         W.files_quick_documents, W.files_quick_pictures })
            {
                Button button = quick;
                button.Click += delegate { openFolder((string)button.Tag); };
            }
            W.files_list.MouseDoubleClick += delegate { openSelectedFolder(); };
            W.files_list.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    openSelectedFolder();
                }
                else if (e.Key == Key.Back)
                {
                    e.Handled = true;
                    openFolder(DeviceEntry.Parent(currentFolder));
                }
            };
            W.files_pull.Click += delegate { pullSelected(); };
            W.files_cancel.Click += delegate { cancelWork(); };

            // Shared
            foreach (Button change in new[] { W.backup_change_folder, W.files_change_folder })
            {
                change.Click += delegate
                {
                    Helper.pathSelect(delegate (string path)
                    {
                        saveRoot = path;
                        updateSaveRootText();
                    });
                };
            }
            foreach (Button open in new[] { W.backup_open_folder, W.files_open_folder })
                open.Click += delegate { openInExplorer(lastOutputFolder ?? saveRoot); };
        }

        // ------------------------------------------------------------------ devices

        static void pollDevices()
        {
            while (true)
            {
                Thread.Sleep(2000);
                if (!pageVisible)
                    continue;

                List<AdbDevice> found;
                try
                {
                    Adb.Result result = Adb.Run("devices -l", Adb.ShortCommand);
                    found = AdbDevice.ParseList(result.Output);
                }
                catch (FileNotFoundException e)
                {
                    log("adb.exe is missing: " + e.FileName);
                    ui(delegate
                    {
                        W.backup_devices_empty.Text = e.Message;
                    });
                    return;
                }
                catch (Exception e)
                {
                    log("adb poll failed: " + e.Message);
                    continue;
                }

                bool same = found.Count == devices.Count && found.Zip(devices,
                    (a, b) => a.Serial == b.Serial && a.State == b.State).All(x => x);
                if (same)
                    continue;

                List<AdbDevice> latest = found;
                ui(delegate { showDevices(latest); });
            }
        }

        static void showDevices(List<AdbDevice> latest)
        {
            devices = latest;
            W.backup_devices.ItemsSource = latest;
            W.backup_devices_empty.Visibility = latest.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            AdbDevice keep = current == null ? null : latest.FirstOrDefault(d => d.Serial == current.Serial);
            if (keep == null)
                keep = latest.FirstOrDefault(d => d.Usable) ?? latest.FirstOrDefault();

            if (keep == null)
            {
                if (!busy)
                    selectDevice(null);
                return;
            }

            W.backup_devices.SelectedItem = keep;
            if (current == null || keep.Serial != current.Serial || keep.State != current.State)
                selectDevice(keep);
        }

        static void selectDevice(AdbDevice device)
        {
            if (busy)
                return;

            current = device;
            root = RootAccess.None;
            partitionDirectory = null;
            partitionRows.Clear();
            fileRows.Clear();
            showDevice();

            if (device != null && device.Usable)
            {
                log("ADB device " + device.Serial + " (" + device.State + (device.Model != null ? ", " + device.Model : "") + ")");
                openFolder("/sdcard");
            }
        }

        static void showDevice()
        {
            bool usable = current != null && current.Usable;
            W.backup_device_note.Text = current == null ? Properties.Resources.backup_no_device
                : current.Unauthorized ? Properties.Resources.backup_unauthorized
                : !current.Usable ? string.Format(Properties.Resources.backup_not_ready, current.State)
                : root == RootAccess.Direct ? Properties.Resources.backup_root_direct
                : root == RootAccess.Su ? Properties.Resources.backup_root_su
                : Properties.Resources.backup_root_unknown;
            W.backup_device_note.Foreground = Palette.Get(usable ? (root != RootAccess.None ? "Ok" : "Dim") : "Warn");
            setBusy(busy);
        }

        static void setBusy(bool value)
        {
            busy = value;
            bool ready = current != null && current.Usable && !value;
            W.backup_devices.IsEnabled = !value;
            W.backup_read.IsEnabled = ready;
            W.backup_select_critical.IsEnabled = !value && partitionRows.Count > 0;
            W.backup_select_none.IsEnabled = !value && partitionRows.Count > 0;
            W.backup_start.IsEnabled = ready && partitionRows.Count > 0;
            W.backup_verify.IsEnabled = !value;
            W.backup_change_folder.IsEnabled = !value;
            W.files_change_folder.IsEnabled = !value;
            W.files_pull.IsEnabled = ready;
            W.files_go.IsEnabled = ready;
            W.files_up.IsEnabled = ready;
            W.files_quick_bar.IsEnabled = ready;
            W.backup_cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            W.files_cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
            {
                Helper.TaskbarItemHelper.start();
            }
            else
            {
                Helper.TaskbarItemHelper.stop();
                W.backup_progress.IsIndeterminate = false;
                W.files_progress.IsIndeterminate = false;
            }
        }

        static void updateSaveRootText()
        {
            W.backup_save_root.Text = saveRoot;
            W.backup_save_root.ToolTip = saveRoot;
            W.files_save_root.Text = saveRoot;
            W.files_save_root.ToolTip = saveRoot;
        }

        /// <summary>Starts a worker unless one is already running. Returns false when busy.</summary>
        static bool run(Action<CancellationToken> work)
        {
            if (busy || current == null)
                return false;

            CancellationTokenSource source = new CancellationTokenSource();
            cancel = source;
            setBusy(true);

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    work(source.Token);
                }
                catch (Exception e)
                {
                    log("backup: " + e.Message);
                    ui(delegate
                    {
                        ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                finally
                {
                    ui(delegate
                    {
                        worker = null;
                        cancel = null;
                        setBusy(false);
                    });
                }
            });
            thread.IsBackground = true;
            worker = thread;
            thread.Start();
            return true;
        }

        static void cancelWork()
        {
            CancellationTokenSource source = cancel;
            if (source != null)
                source.Cancel();
            Adb running = activeAdb;
            if (running != null)
                running.Abort();
        }

        /// <summary>Called when the window closes: stop any transfer, then the adb server.</summary>
        public static void shutdown()
        {
            cancelWork();
            Thread running = worker;
            if (running != null)
                running.Join(TimeSpan.FromSeconds(10));
            Adb.StopServer();
        }

        // ------------------------------------------------------------------ partitions

        /// <summary>Finds out how root can be had, asking su if adb itself is not root.</summary>
        static RootAccess probeRoot(string serial, CancellationToken token)
        {
            Adb.Result id = Adb.Run(AdbCommand.Shell(serial, "id -u"), Adb.ShortCommand);
            if (id.Succeeded && id.Output.Trim() == "0")
                return RootAccess.Direct;

            token.ThrowIfCancellationRequested();
            log(Properties.Resources.backup_asking_root);
            Adb.Result su = Adb.Run(AdbCommand.Shell(serial, AdbCommand.AsRoot("id -u", RootAccess.Su)), Adb.RootPrompt);
            return su.Succeeded && su.Output.Trim() == "0" ? RootAccess.Su : RootAccess.None;
        }

        static void readPartitionTable()
        {
            string serial = current.Serial;
            run(delegate (CancellationToken token)
            {
                ui(delegate { W.backup_progress.IsIndeterminate = true; });

                RootAccess access = probeRoot(serial, token);
                if (access == RootAccess.None)
                {
                    log("no root on " + serial);
                    ui(delegate
                    {
                        root = RootAccess.None;
                        showDevice();
                        ThemedDialog.Show(Properties.Resources.backup_needs_root, Properties.Resources.nav_backup,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }

                Adb.Result listing = Adb.Run(
                    AdbCommand.Shell(serial, AdbCommand.AsRoot(AdbCommand.ListPartitionsScript, access)), Adb.RootPrompt);
                string directory;
                List<DevicePartition> partitions = DevicePartition.ParseListing(listing.Output, out directory);
                log("partition table of " + serial + ": " + partitions.Count + " partitions in " + (directory ?? "?"));

                ui(delegate
                {
                    root = access;
                    partitionDirectory = directory;
                    partitionRows.Clear();
                    foreach (DevicePartition partition in partitions)
                        partitionRows.Add(new BackupRow(partition) { Selected = partition.IsCritical });
                    applyFilter();
                    showDevice();

                    if (directory == null || partitions.Count == 0)
                    {
                        ThemedDialog.Show(Properties.Resources.backup_no_table + "\n\n" + listing.Problem,
                            Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
        }

        static void applyFilter()
        {
            string text = W.backup_filter.Text.Trim();
            System.ComponentModel.ICollectionView view =
                System.Windows.Data.CollectionViewSource.GetDefaultView(partitionRows);
            view.Filter = text.Length == 0 ? null
                : new Predicate<object>(item => ((BackupRow)item).Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string safeFileName(string value)
        {
            StringBuilder name = new StringBuilder();
            foreach (char c in value ?? "")
                name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            return name.Length == 0 ? "device" : name.ToString();
        }

        static void startPartitionBackup()
        {
            List<BackupRow> chosen = partitionRows.Where(row => row.Selected).ToList();
            if (chosen.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.backup_nothing_selected);
                return;
            }

            List<BackupRow> large = chosen.Where(row => row.Partition.IsLarge).ToList();
            if (large.Count > 0 && !Helper.confirm(string.Format(Properties.Resources.backup_large_warning,
                    string.Join(", ", large.Select(row => row.Name + " (" + row.Size + ")"))),
                    Properties.Resources.confirm_title, true))
                return;

            AdbDevice device = current;
            RootAccess access = root;
            string directory = partitionDirectory;
            string folder = Path.Combine(saveRoot, "Backups",
                safeFileName(device.Model ?? device.Product ?? "device") + "_" + safeFileName(device.Serial)
                + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            foreach (BackupRow row in partitionRows)
            {
                row.Progress = 0;
                row.Current = row.Selected ? BackupRow.Stage.Queued : BackupRow.Stage.Idle;
            }

            long totalBytes = Math.Max(1, chosen.Sum(row => Math.Max(0, row.Partition.Size)));
            W.backup_progress.Value = 0;

            run(delegate (CancellationToken token)
            {
                Directory.CreateDirectory(folder);
                log("Backing up " + chosen.Count + " partitions of " + device.Serial + " to " + folder);

                List<KeyValuePair<string, string>> hashes = new List<KeyValuePair<string, string>>();
                List<string> failures = new List<string>();
                StringBuilder info = new StringBuilder();
                long doneBytes = 0;
                Stopwatch clock = Stopwatch.StartNew();

                foreach (BackupRow row in chosen)
                {
                    if (token.IsCancellationRequested)
                    {
                        ui(delegate { row.Current = BackupRow.Stage.Skipped; });
                        continue;
                    }

                    ui(delegate { row.Current = BackupRow.Stage.Reading; });
                    string fileName = row.Name + ".img";
                    string target = Path.Combine(folder, fileName);
                    long expected = row.Partition.Size;
                    long before = doneBytes;

                    try
                    {
                        HashingCopy.Result copied;
                        using (Adb adb = new Adb(AdbCommand.ExecOut(device.Serial,
                            AdbCommand.AsRoot(AdbCommand.ReadPartitionCommand(directory, row.Name), access)), Fastboot.NoLimit))
                        {
                            activeAdb = adb;
                            if (token.IsCancellationRequested)
                                adb.Abort();
                            using (FileStream file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            {
                                copied = HashingCopy.Copy(adb.Output, file, new Progress(delegate (long bytes)
                                {
                                    double percent = expected > 0 ? Math.Min(100, bytes * 100.0 / expected) : 50;
                                    long overall = before + Math.Min(bytes, Math.Max(0, expected));
                                    ui(delegate
                                    {
                                        row.Progress = percent;
                                        W.backup_progress.Value = overall * 100.0 / totalBytes;
                                        W.backup_progress_text.Text = row.Name + "  ·  " + ByteSize.Format(bytes)
                                            + (expected > 0 ? " / " + ByteSize.Format(expected) : "");
                                    });
                                }), token);
                            }
                            adb.WaitForExit();
                            activeAdb = null;
                        }

                        token.ThrowIfCancellationRequested();
                        if (copied.Bytes == 0 || (expected > 0 && copied.Bytes != expected))
                        {
                            throw new IOException(string.Format(Properties.Resources.backup_short_read,
                                ByteSize.Format(copied.Bytes), expected > 0 ? ByteSize.Format(expected) : "?"));
                        }

                        hashes.Add(new KeyValuePair<string, string>(fileName, copied.Sha256));
                        info.Append(row.Name).Append('\t').Append(copied.Bytes).Append('\t').Append(copied.Sha256).Append('\n');
                        doneBytes += Math.Max(0, expected);
                        log("  " + row.Name + ": " + ByteSize.Format(copied.Bytes) + "  sha256 " + copied.Sha256);
                        ui(delegate
                        {
                            row.Progress = 100;
                            row.Current = BackupRow.Stage.Saved;
                        });
                    }
                    catch (Exception e)
                    {
                        activeAdb = null;
                        deleteQuietly(target);
                        bool cancelled = token.IsCancellationRequested;
                        if (!cancelled)
                        {
                            failures.Add(row.Name + ": " + e.Message);
                            log("  " + row.Name + " failed: " + e.Message);
                        }
                        ui(delegate { row.Current = cancelled ? BackupRow.Stage.Skipped : BackupRow.Stage.Failed; });
                        doneBytes = before + Math.Max(0, expected);
                    }
                }

                if (hashes.Count > 0)
                {
                    File.WriteAllText(Path.Combine(folder, Sha256Sums.FileName), Sha256Sums.Format(hashes));
                    File.WriteAllText(Path.Combine(folder, "backup-info.txt"),
                        "Fastboot Studio backup\n" +
                        "date\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "\n" +
                        "serial\t" + device.Serial + "\n" +
                        "model\t" + (device.Model ?? "") + "\n" +
                        "product\t" + (device.Product ?? "") + "\n" +
                        "mode\t" + device.State + "\n" +
                        "source\t" + directory + "\n\n" +
                        "partition\tbytes\tsha256\n" + info);
                }
                else
                {
                    tryRemoveEmptyFolder(folder);
                }

                bool wasCancelled = token.IsCancellationRequested;
                log("Backup finished in " + clock.Elapsed.TotalSeconds.ToString("F1") + " s: "
                    + hashes.Count + " saved, " + failures.Count + " failed" + (wasCancelled ? ", cancelled" : ""));

                ui(delegate
                {
                    W.backup_progress_text.Text = "";
                    if (hashes.Count > 0)
                        lastOutputFolder = folder;

                    if (wasCancelled)
                    {
                        ThemedDialog.Show(Properties.Resources.backup_cancelled, Properties.Resources.nav_backup,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else if (failures.Count > 0)
                    {
                        Helper.TaskbarItemHelper.error();
                        ThemedDialog.Show(string.Format(Properties.Resources.backup_partial, hashes.Count, chosen.Count, folder)
                            + "\n\n" + string.Join("\n", failures), Properties.Resources.error,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else
                    {
                        W.backup_progress.Value = 100;
                        ThemedDialog.Done(string.Format(Properties.Resources.backup_done, hashes.Count, folder));
                    }
                });
            });
        }

        sealed class Progress : IProgress<long>
        {
            readonly Action<long> action;
            public Progress(Action<long> action) { this.action = action; }
            public void Report(long value) { action(value); }
        }

        static void deleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static void tryRemoveEmptyFolder(string path)
        {
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static void verifyBackup()
        {
            Helper.pathSelect(delegate (string folder)
            {
                if (busy)
                    return;
                setBusy(true);
                W.backup_progress.IsIndeterminate = true;
                Helper.offloadAndRun(delegate
                {
                    VerifyReport report;
                    try
                    {
                        report = Sha256Sums.Verify(folder);
                    }
                    catch (Exception e)
                    {
                        log("verify failed: " + e.Message);
                        report = new VerifyReport(true, new List<string>(), new List<string> { e.Message });
                    }

                    log("Checked " + folder + ": " + report.Good.Count + " good, " + report.Bad.Count + " bad");
                    W.Dispatcher.Invoke(delegate
                    {
                        setBusy(false);
                        if (!report.HadSums)
                            ThemedDialog.Show(Properties.Resources.backup_verify_none, Properties.Resources.backup_verify_title,
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        else if (report.AllGood)
                            ThemedDialog.Done(string.Format(Properties.Resources.backup_verify_ok, report.Good.Count));
                        else
                            ThemedDialog.Show(string.Format(Properties.Resources.backup_verify_bad, report.Bad.Count, report.Total)
                                + "\n\n" + string.Join("\n", report.Bad), Properties.Resources.backup_verify_title,
                                MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }, delegate { });
            });
        }

        // ------------------------------------------------------------------ files

        static void openSelectedFolder()
        {
            FileRow row = W.files_list.SelectedItem as FileRow;
            if (row != null && (row.Entry.IsFolder || row.Entry.IsLink))
                openFolder(row.Entry.Path);
        }

        static void openFolder(string folder)
        {
            if (current == null || !current.Usable || string.IsNullOrEmpty(folder) || busy)
                return;
            if (!folder.StartsWith("/", StringComparison.Ordinal))
                folder = "/" + folder;

            string serial = current.Serial;
            string target = folder;
            W.files_path.Text = target;
            run(delegate (CancellationToken token)
            {
                ui(delegate { W.files_progress.IsIndeterminate = true; });
                Adb.Result listing = Adb.Run(AdbCommand.Shell(serial, AdbCommand.ListFolderCommand(target)), Adb.ShortCommand);
                List<DeviceEntry> entries = DeviceEntry.ParseListing(listing.Output);
                ui(delegate
                {
                    currentFolder = target;
                    fileRows.Clear();
                    foreach (DeviceEntry entry in entries)
                        fileRows.Add(new FileRow(entry));
                    W.files_empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    W.files_status.Text = string.Format(Properties.Resources.files_count,
                        entries.Count(e => e.IsFolder), entries.Count(e => !e.IsFolder));
                });
            });
        }

        static void pullSelected()
        {
            List<FileRow> chosen = W.files_list.SelectedItems.Cast<FileRow>().ToList();
            if (chosen.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.files_nothing_selected);
                return;
            }

            AdbDevice device = current;
            string folder = Path.Combine(saveRoot, "Files", safeFileName(device.Model ?? device.Serial) + "_" + safeFileName(device.Serial));

            run(delegate (CancellationToken token)
            {
                Directory.CreateDirectory(folder);
                ui(delegate { W.files_progress.IsIndeterminate = true; });
                List<string> failures = new List<string>();
                int copied = 0;

                foreach (FileRow row in chosen)
                {
                    if (token.IsCancellationRequested)
                        break;

                    string remote = row.Entry.Path;
                    ui(delegate { W.files_status.Text = string.Format(Properties.Resources.files_copying, row.Name); });
                    log("> adb pull " + remote);

                    using (Adb adb = new Adb(AdbCommand.Pull(device.Serial, remote, folder), Fastboot.NoLimit))
                    {
                        activeAdb = adb;
                        if (token.IsCancellationRequested)
                            adb.Abort();
                        string output = adb.ReadOutput();
                        int? exit = adb.WaitForExit();
                        activeAdb = null;
                        foreach (string line in output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            log("  " + line.Trim());

                        if (token.IsCancellationRequested)
                            break;
                        if (exit == 0)
                        {
                            copied++;
                        }
                        else
                        {
                            string problem = new Adb.Result { ExitCode = exit, Output = output, Errors = adb.Errors }.Problem;
                            failures.Add(row.Name + ": " + problem);
                            log("  failed: " + problem);
                        }
                    }
                }

                bool cancelled = token.IsCancellationRequested;
                ui(delegate
                {
                    W.files_status.Text = "";
                    if (copied > 0)
                        lastOutputFolder = folder;

                    if (cancelled)
                        ThemedDialog.Show(Properties.Resources.backup_cancelled, Properties.Resources.nav_backup,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    else if (failures.Count > 0)
                        ThemedDialog.Show(string.Format(Properties.Resources.files_partial, copied, chosen.Count, folder)
                            + "\n\n" + string.Join("\n", failures), Properties.Resources.error,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    else
                        ThemedDialog.Done(string.Format(Properties.Resources.files_done, copied, folder));
                });
            });
        }

        static void openInExplorer(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", AdbCommand.WindowsArgument(folder)) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
