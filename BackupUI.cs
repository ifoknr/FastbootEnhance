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
using FastbootEnhance.Core.Images;

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

            // Phone in fastboot
            W.backup_fb_reboot_system.Click += delegate { fastbootReboot("reboot"); };
            W.backup_fb_reboot_recovery.Click += delegate { fastbootReboot("reboot recovery"); };
            W.backup_fb_boot_image.Click += delegate { bootRecoveryImage(); };

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

                // A phone in the bootloader or fastbootd is invisible to adb. Look for it there
                // too, so the page can say what is wrong instead of waiting forever. Not while a
                // flash is running: that fastboot has the phone and must not be disturbed.
                List<string> inFastboot = found.Any(d => d.Usable) || FastbootUI.flashing
                    ? new List<string>() : listFastboot();

                bool same = found.Count == devices.Count && found.Zip(devices,
                    (a, b) => a.Serial == b.Serial && a.State == b.State).All(x => x);
                List<AdbDevice> latest = found;
                ui(delegate
                {
                    if (!same)
                        showDevices(latest);
                    showFastboot(inFastboot);
                });
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

        // ------------------------------------------------------------------ phone in fastboot

        static List<string> fastbootSerials = new List<string>();

        /// <summary>The phone last rebooted or booted from this panel, and when.</summary>
        static string fastbootTarget;
        static DateTime fastbootLeftAt = DateTime.MinValue;

        /// <summary>How long the panel waits for a rebooted phone to show up on adb.</summary>
        static readonly TimeSpan RebootWait = TimeSpan.FromMinutes(2);

        static volatile bool fastbootBusy;

        /// <summary>
        /// Set when a reboot or boot was sent; once the phone has dropped off fastboot, seeing it
        /// there again means it came back (a reboot to the bootloader, or a failed start).
        /// </summary>
        static bool fastbootSentAway;
        static bool fastbootGone;

        static List<string> listFastboot()
        {
            try
            {
                return Fastboot.ListSerials();
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception e)
            {
                log("fastboot poll failed: " + e.Message);
            }
            return new List<string>();
        }

        static int? runFastboot(string serial, string action, TimeSpan timeout, out string output)
        {
            return Fastboot.Run(serial, action, timeout, out output);
        }

        /// <summary>
        /// Shows the fastboot panel in place of the backup tabs while a phone sits in fastboot
        /// with nothing usable on adb, or while a phone sent away from it is still restarting.
        /// </summary>
        static void showFastboot(List<string> serials)
        {
            fastbootSerials = serials;
            bool onAdb = current != null && current.Usable;
            if (onAdb)
            {
                fastbootLeftAt = DateTime.MinValue;
                fastbootSentAway = false;
            }
            else if (fastbootSentAway && !fastbootBusy)
            {
                if (serials.Count == 0)
                {
                    fastbootGone = true;
                }
                else if (fastbootGone)
                {
                    fastbootSentAway = false;
                    fastbootLeftAt = DateTime.MinValue;
                    W.backup_fb_status.Text = "";
                }
            }

            bool restarting = DateTime.Now - fastbootLeftAt < RebootWait;
            bool show = !onAdb && !busy && (serials.Count > 0 || restarting || fastbootBusy);

            W.backup_fastboot_panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            W.backup_tabs.Visibility = show ? Visibility.Hidden : Visibility.Visible;
            if (!show)
            {
                if (!fastbootBusy)
                    W.backup_fb_status.Text = "";
                return;
            }

            string serial = serials.Count > 0 ? serials[0] : fastbootTarget;
            W.backup_fb_title.Text = string.Format(Properties.Resources.backup_fb_title, Helper.ltr(serial ?? ""));
            bool ready = serials.Count > 0 && !fastbootBusy;
            W.backup_fb_reboot_system.IsEnabled = ready;
            W.backup_fb_reboot_recovery.IsEnabled = ready;
            W.backup_fb_boot_image.IsEnabled = ready;
        }

        /// <summary>Runs one fastboot job on a worker thread, with the panel's buttons off.</summary>
        static void fastbootJob(string serial, string status, Action work)
        {
            if (fastbootBusy)
                return;
            fastbootBusy = true;
            fastbootTarget = serial;
            W.backup_fb_status.Text = status;
            showFastboot(fastbootSerials);

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    log("fastboot (" + serial + "): " + e.Message);
                    ui(delegate { W.backup_fb_status.Text = ""; });
                    ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ui(delegate
                    {
                        fastbootBusy = false;
                        showFastboot(fastbootSerials);
                    });
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>The last few lines fastboot printed, which is where it says what failed.</summary>
        static string fastbootTail(string output)
        {
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            return lines.Length == 0 ? "?" : string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 4)));
        }

        /// <summary>Restarts the phone out of fastboot; reboot alone for Android, or "reboot recovery".</summary>
        static void fastbootReboot(string action)
        {
            string serial = fastbootSerials.FirstOrDefault();
            if (serial == null)
                return;

            fastbootJob(serial, string.Format(Properties.Resources.backup_fb_rebooting, Helper.ltr(serial)), delegate
            {
                string output;
                int? code = runFastboot(serial, action, Fastboot.ShortCommand, out output);
                log("fastboot " + action + " (" + serial + "): " + (code == 0 ? "OK" : fastbootTail(output)));
                if (code != 0)
                    throw new InvalidOperationException(string.Format(Properties.Resources.backup_fb_failed, fastbootTail(output)));
                ui(delegate { sentAway(true); });
            });
        }

        /// <summary>A reboot or boot went through; <paramref name="leaving"/> when it leaves fastboot.</summary>
        static void sentAway(bool leaving)
        {
            fastbootSentAway = true;
            fastbootGone = false;
            if (leaving)
                fastbootLeftAt = DateTime.Now;
        }

        /// <summary>True when "getvar is-userspace" says the phone is in fastbootd.</summary>
        static bool isUserspace(string output)
        {
            foreach (string line in output.Split('\n'))
            {
                int at = line.IndexOf("is-userspace", StringComparison.Ordinal);
                if (at < 0)
                    continue;
                string value = line.Substring(at + "is-userspace".Length).TrimStart(':', ' ').Trim();
                return value.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// Sends a TWRP or OrangeFox image with "fastboot boot": the phone starts it once from
        /// memory, nothing is flashed, and the recovery then serves adb as root.
        /// </summary>
        static void bootRecoveryImage()
        {
            string serial = fastbootSerials.FirstOrDefault();
            if (serial == null || fastbootBusy)
                return;

            Helper.fileSelect(delegate (string path)
            {
                ImageInfo info;
                try
                {
                    info = ImageProbe.Identify(path);
                }
                catch (Exception e)
                {
                    ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (info.IsSparse || info.Kind != ImageKind.BootImage)
                {
                    ThemedDialog.Show(Properties.Resources.backup_fb_not_boot_image, Properties.Resources.error,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (BootImageHeader.Read(path).Kind == BootImageKind.NoKernel)
                {
                    ThemedDialog.Show(Properties.Resources.boot_once_no_kernel, Properties.Resources.error,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string name = Path.GetFileName(path);
                fastbootJob(serial, string.Format(Properties.Resources.backup_fb_sending, Helper.ltr(name)), delegate
                {
                    string vars;
                    runFastboot(serial, "getvar is-userspace", Fastboot.ShortCommand, out vars);
                    if (isUserspace(vars))
                    {
                        log("fastboot boot: " + serial + " is in fastbootd");
                        if (!Helper.confirm(Properties.Resources.backup_fb_userspace, Properties.Resources.nav_backup))
                        {
                            ui(delegate { W.backup_fb_status.Text = ""; });
                            return;
                        }
                        string rebootOutput;
                        int? rebootCode = runFastboot(serial, "reboot bootloader", Fastboot.ShortCommand, out rebootOutput);
                        log("fastboot reboot bootloader (" + serial + "): " + (rebootCode == 0 ? "OK" : fastbootTail(rebootOutput)));
                        if (rebootCode != 0)
                            throw new InvalidOperationException(string.Format(Properties.Resources.backup_fb_failed, fastbootTail(rebootOutput)));
                        ui(delegate
                        {
                            sentAway(false);
                            W.backup_fb_status.Text = string.Format(Properties.Resources.backup_fb_rebooting, Helper.ltr(serial));
                        });
                        return;
                    }

                    string output;
                    int? code = runFastboot(serial, "boot \"" + path + "\"", Fastboot.LongCommand, out output);
                    log("fastboot boot " + name + " (" + serial + "): " + (code == 0 ? "OK" : fastbootTail(output)));
                    if (code != 0)
                        throw new InvalidOperationException(string.Format(Properties.Resources.backup_fb_boot_failed, fastbootTail(output)));

                    ui(delegate
                    {
                        sentAway(true);
                        W.backup_fb_status.Text = Properties.Resources.backup_fb_booted;
                    });
                    ThemedDialog.Done(Properties.Resources.backup_fb_booted);
                });
            }, "Boot / recovery images|*.img;*.bin|All files|*.*");
        }

        // ------------------------------------------------------------------ adb device

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
                if (device.State == "device")
                    rememberFacts(device.Serial);
                openFolder("/sdcard");
            }
        }

        /// <summary>
        /// Notes the phone's kernel and security patch, which fastboot cannot read, so the
        /// device page can check a boot image against them later. Runs in the background and
        /// never gets in the way: a phone that does not answer is simply not remembered.
        /// </summary>
        static void rememberFacts(string serial)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Adb.Result result = Adb.Run(AdbCommand.Shell(serial, DeviceFacts.Command), Adb.ShortCommand);
                    if (!result.Succeeded)
                        return;
                    DeviceFacts facts = DeviceFacts.Parse(serial, result.Output, DateTime.UtcNow);
                    if (facts.Empty)
                        return;
                    Settings.Set(FastbootUI.DeviceFactsKey, DeviceFacts.Remember(Settings.Get(FastbootUI.DeviceFactsKey), facts));
                    log("ADB device " + serial + ": kernel " + (facts.Kernel ?? "?") + ", security patch " + (facts.Patch ?? "?"));
                }
                catch (Exception e)
                {
                    log("ADB device " + serial + ": kernel not read: " + e.Message);
                }
            });
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
