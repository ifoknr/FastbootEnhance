using ChromeosUpdateEngine;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Images;
using FastbootEnhance.Core.Payload;
using FastbootEnhance.Core.Usb;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace FastbootEnhance
{
    class FastbootUI
    {
        /// <summary>
        /// Staging area for images on their way to the device. It lives under the user's temp
        /// directory rather than next to the executable, which may sit somewhere unwritable.
        /// </summary>
        public static readonly string PAYLOAD_TMP =
            Path.Combine(Path.GetTempPath(), "FastbootStudio", "flash");

        static List<fastboot_devices_row> devices;
        static string cur_serial;
        static FastbootData fastbootData;

        /// <summary>True while a payload is being extracted or written; the window asks before closing.</summary>
        public static volatile bool flashing;

        static void appendLog(string logs)
        {
            LogStore.Append(logs);
        }

        enum FastbootStatus
        {
            show_devices,
            show_actions
        }

        static FastbootStatus cur_status;
        static void refreshDeviceList()
        {
            MainWindow.THIS.Dispatcher.Invoke(new Action(delegate
            {
                MainWindow.THIS.fastboot_devices_list.Items.Clear();
                foreach (fastboot_devices_row row in devices)
                {
                    MainWindow.THIS.fastboot_devices_list.Items.Add(row);
                }
                MainWindow.THIS.fastboot_devices_empty.Visibility =
                    devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                MainWindow.THIS.main_tabs.Tag = string.Format(Properties.Resources.rail_devices, devices.Count);
            }));
        }

        static bool checkCurDevExist()
        {
            using (Fastboot fastboot = new Fastboot(null, "devices"))
            {
                while (true)
                {
                    string line = fastboot.stdout.ReadLine();
                    if (line == null)
                        break;

                    string[] param = line.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (param.Length > 0 && cur_serial == param[0])
                        return true;
                }
                ThemedDialog.Show(Properties.Resources.fastboot_device_not_exist);
                leaveDevice();
                return false;
            }
        }

        /// <summary>
        /// Back to the device list. The variables read from the device go too, so nothing later
        /// (a payload flash, the pre-flash checks) can act on what an earlier device reported.
        /// </summary>
        static void leaveDevice()
        {
            cur_serial = null;
            fastbootData = null;
            cur_status = FastbootStatus.show_devices;
            change_page();
        }

        static void devicesListRefresher()
        {
            while (true)
            {
                Thread.Sleep(1000);

                if (cur_status == FastbootStatus.show_actions)
                    continue;

                List<fastboot_devices_row> tmp;
                try
                {
                    tmp = listDevices();
                }
                catch (FileNotFoundException e)
                {
                    // Without fastboot.exe there is nothing to poll; say so once and stop,
                    // rather than letting the exception take the whole app down.
                    MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        MainWindow.THIS.main_tabs.Tag = Properties.Resources.rail_no_fastboot;
                        ThemedDialog.Show(e.Message + "\n" + e.FileName, Properties.Resources.error,
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                    return;
                }
                catch (Exception e)
                {
                    appendLog("device poll failed: " + e.Message);
                    continue;
                }

                if (tmp.Count != devices.Count)
                {
                    devices = tmp;
                    refreshDeviceList();
                }
                else
                {
                    int i;
                    for (i = 0; i < tmp.Count; i++)
                    {
                        if (devices[i].name != tmp[i].name || devices[i].serial != tmp[i].serial)
                            break;
                    }
                    if (i != tmp.Count)
                    {
                        devices = tmp;
                        refreshDeviceList();
                    }
                }
            }
        }

        static List<fastboot_devices_row> listDevices()
        {
            List<fastboot_devices_row> found = new List<fastboot_devices_row>();

            using (Fastboot fastboot = new Fastboot(null, "devices"))
            {
                while (true)
                {
                    string line = fastboot.stdout.ReadLine();
                    if (line == null)
                        break;

                    string[] param = line.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (param.Length < 2)
                        continue;

                    found.Add(new fastboot_devices_row(param[0], param[1]));
                }
            }

            return found;
        }

        class fastboot_devices_row
        {
            public string serial { get; }
            public string name { get; }
            public fastboot_devices_row(string serial, string name)
            {
                this.serial = serial;
                this.name = name;
            }
        }

        class fastboot_info_row
        {
            public string name { get; }
            public string value { get; }
            public fastboot_info_row(string name, string value)
            {
                this.name = name;
                this.value = Helper.ltr(value);
            }
        }

        class fastboot_partition_row
        {
            public string name { get; }
            public string size { get; }
            public string is_logical { get; }
            public fastboot_partition_row(string name, string size, string is_logical)
            {
                this.name = name;
                this.size = size;
                this.is_logical = is_logical;
            }
        }

        /// <summary>
        /// How many operations hold the controls. Each lock is matched by exactly one unlock,
        /// and the controls come back only when the last one finishes.
        /// </summary>
        static int lockDepth;

        static void action_lock()
        {
            lockDepth++;
            if (lockDepth > 1)
                return;
            MainWindow.THIS.fastboot_progress_bar.Value = 0;
            MainWindow.THIS.fastboot_action_bar.IsEnabled = false;
            MainWindow.THIS.fastboot_progress_bar.Visibility = Visibility.Visible;
            MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
            Helper.TaskbarItemHelper.start();
            MainWindow.THIS.fastboot_single_part_op.IsEnabled = false;
            MainWindow.THIS.fastboot_flash_payload.IsEnabled = false;
            // Leaving the device mid-operation would let a flash continue with no target serial.
            MainWindow.THIS.fastboot_remove.IsEnabled = false;
        }

        static void action_unlock()
        {
            if (lockDepth > 0)
                lockDepth--;
            if (lockDepth > 0)
                return;
            MainWindow.THIS.fastboot_progress_bar.Visibility = Visibility.Hidden;
            MainWindow.THIS.fastboot_action_bar.IsEnabled = true;
            Helper.TaskbarItemHelper.stop();
            MainWindow.THIS.fastboot_single_part_op.IsEnabled = true;
            MainWindow.THIS.fastboot_flash_payload.IsEnabled = true;
            MainWindow.THIS.fastboot_remove.IsEnabled = true;
        }

        static Helper.ListHelper<fastboot_partition_row> listHelper;

        /// <summary>Long enough for a phone rebooting into fastbootd to come back.</summary>
        static readonly TimeSpan GetvarTimeout = TimeSpan.FromMinutes(3);

        static void load_fastboot_vars()
        {
            //reset
            fastbootData = null;
            listHelper.clear();
            MainWindow.THIS.fastboot_partition_name_textbox.Text = "";
            MainWindow.THIS.fastboot_info_list.Items.Clear();
            MainWindow.THIS.fastboot_checks.ItemsSource = null;
            action_lock();
            MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = true;

            string serial = cur_serial;
            Thread loader = new Thread(new ThreadStart(delegate
            {
                FastbootData loaded;
                try
                {
                    using (Fastboot fastboot = new Fastboot(serial, "getvar all", GetvarTimeout))
                    {
                        // fastboot bug: Must read stderr first or stdout would be blocked
                        string output = fastboot.stderr.ReadToEnd();
                        int? exitCode = fastboot.WaitForExit();
                        if (exitCode == null)
                        {
                            throw new TimeoutException("the device did not answer \"getvar all\" within "
                                + GetvarTimeout.TotalMinutes + " minutes");
                        }

                        loaded = new FastbootData(output);

                        // Some old bootloaders reject "all" but still list a few variables; only
                        // a failure that told us nothing at all is treated as one.
                        bool learned = loaded.product != null || loaded.current_slot != null
                            || loaded.partition_size.Count > 0 || loaded.max_download_size > 0;
                        if (exitCode != 0 && !learned)
                        {
                            string[] lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            throw new IOException("fastboot getvar all exited with code " + exitCode
                                + (lines.Length > 0 ? ": " + lines[lines.Length - 1].Trim() : ""));
                        }
                    }
                }
                catch (Exception e)
                {
                    appendLog("getvar all failed: " + e.Message);
                    MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
                        action_unlock();
                        if (cur_serial != serial)
                            return;
                        ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                        leaveDevice();
                    }));
                    return;
                }

                MainWindow.THIS.Dispatcher.Invoke(delegate
                {
                    MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
                    action_unlock();
                    if (cur_serial != serial)
                        return;
                    fastbootData = loaded;
                    showFastbootVars();
                });
            }));
            loader.IsBackground = true;
            loader.Start();
        }

        static void showFastbootVars()
        {
            //Partition list init

            foreach (string key in fastbootData.partition_size.Keys)
            {
                long raw_size = fastbootData.partition_size[key];
                string size_str = raw_size >= 0 ? Helper.byte2AUnit((ulong)raw_size) : Properties.Resources.fastboot_0_size;
                bool? raw_logical = null;
                fastbootData.partition_is_logical.TryGetValue(key, out raw_logical);
                string logical_str = raw_logical != null && raw_logical == true ? Properties.Resources.yes : Properties.Resources.no;
                listHelper.addItem(new fastboot_partition_row(key, size_str, logical_str));
            }
            listHelper.render();

            //info list init

            MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_device,
                fastbootData.product ?? Properties.Resources.unknown));

            MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_secure_boot,
                fastbootData.secure ? Properties.Resources.enabled : Properties.Resources.disabled));

            MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_seamless_update,
                fastbootData.current_slot != null ? Properties.Resources.yes : Properties.Resources.no));

            if (fastbootData.current_slot != null)
                MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_current_slot,
                    fastbootData.current_slot));

            MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_is_userspace,
                fastbootData.fastbootd ? Properties.Resources.yes : Properties.Resources.no));

            if (fastbootData.max_download_size > 0)
                MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row("max-download-size",
                    Helper.byte2AUnit(fastbootData.max_download_size)));

            string vab_status_str = null;
            switch (fastbootData.snapshot_update_status)
            {
                case "none":
                    vab_status_str = Properties.Resources.fastboot_update_status_none;
                    break;
                case "snapshotted":
                    vab_status_str = Properties.Resources.fastboot_update_status_snapshotted;
                    break;
                case "merging":
                    vab_status_str = Properties.Resources.fastboot_update_status_merging;
                    break;
                default:
                    vab_status_str = fastbootData.snapshot_update_status;
                    break;
            }

            if (vab_status_str != null)
                MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_update_status,
                    vab_status_str));

            //buttons init

            MainWindow.THIS.fastboot_logical_create.IsEnabled = fastbootData.fastbootd;
            MainWindow.THIS.fastboot_reboot_d.Content = fastbootData.fastbootd ?
                Properties.Resources.fastboot_reboot_bootloader : Properties.Resources.fastboot_reboot_fastbootd;

            if (fastbootData.current_slot == "a" || fastbootData.current_slot == "b")
            {
                MainWindow.THIS.fastboot_ab_switch.Visibility = Visibility.Visible;
                MainWindow.THIS.fastboot_ab_switch.Content = fastbootData.current_slot == "a"
                    ? Properties.Resources.fastboot_setactive_b
                    : Properties.Resources.fastboot_setactive_a;
            }
            else
            {
                MainWindow.THIS.fastboot_ab_switch.Visibility = Visibility.Collapsed;
            }

            // Only offer to cancel a Virtual A/B update when there is one. The old code hid this
            // button the first time it saw "none" and never showed it again.
            MainWindow.THIS.fastboot_cancel_update.Visibility =
                fastbootData.HasPendingUpdate ? Visibility.Visible : Visibility.Collapsed;

            MainWindow.THIS.fastboot_mode_text.Text = fastbootData.fastbootd ? "fastbootd" : "bootloader";
            MainWindow.THIS.fastboot_mode_badge.Visibility = Visibility.Visible;

            MainWindow.THIS.fastboot_checks.ItemsSource = buildChecks();
            showSuperSpace();
        }

        /// <summary>What to look at before writing anything to this device.</summary>
        static List<CheckRow> buildChecks()
        {
            List<CheckRow> checks = new List<CheckRow>();

            if (fastbootData.unlocked == true)
                checks.Add(new CheckRow(CheckRow.Level.Ok, Properties.Resources.check_unlocked));
            else if (fastbootData.unlocked == false)
                checks.Add(new CheckRow(CheckRow.Level.Danger, Properties.Resources.check_locked));

            if (fastbootData.fastbootd)
                checks.Add(new CheckRow(CheckRow.Level.Ok, Properties.Resources.check_fastbootd));
            else
                checks.Add(new CheckRow(CheckRow.Level.Warn, Properties.Resources.check_bootloader));

            if (!fastbootData.HasPendingUpdate)
                checks.Add(new CheckRow(CheckRow.Level.Ok, Properties.Resources.check_no_update));
            else
                checks.Add(new CheckRow(CheckRow.Level.Warn,
                    string.Format(Properties.Resources.check_update_pending, fastbootData.snapshot_update_status)));

            if (fastbootData.HasCowPartitions)
                checks.Add(new CheckRow(CheckRow.Level.Warn, Properties.Resources.check_cow));

            if (fastbootData.current_slot != null)
                checks.Add(new CheckRow(CheckRow.Level.Ok,
                    string.Format(Properties.Resources.check_slot, fastbootData.current_slot)));

            return checks;
        }

        /// <summary>The "Super" card on the partition tab: what each slot takes and what is free.</summary>
        static void showSuperSpace()
        {
            SuperSpace space = SuperSpace.From(fastbootData);
            MainWindow.THIS.fastboot_super_bar.Value = 0;
            MainWindow.THIS.fastboot_super_bar.Foreground = Palette.Get("Accent");

            if (!fastbootData.fastbootd)
            {
                MainWindow.THIS.fastboot_super_text.Text = Properties.Resources.super_space_bootloader;
                return;
            }
            if (!space.Known)
            {
                MainWindow.THIS.fastboot_super_text.Text = Properties.Resources.super_space_unknown;
                return;
            }

            MainWindow.THIS.fastboot_super_bar.Value = Math.Min(100, space.Used * 100.0 / space.SuperSize);
            MainWindow.THIS.fastboot_super_bar.Foreground = Palette.Get(
                space.Free < (512L << 20) ? "Danger" : space.Free < (2L << 30) ? "Warn" : "Accent");

            List<string> lines = new List<string>();
            lines.Add(string.Format(Properties.Resources.super_space_total,
                Helper.byte2AUnit(space.SuperSize), Helper.byte2AUnit(space.Free)));
            if (space.CurrentSlot != null)
                lines.Add(string.Format(Properties.Resources.super_space_slot, space.CurrentSlot,
                    Helper.byte2AUnit(space.CurrentSlotBytes)) + "  " + Properties.Resources.super_space_in_use);
            if (space.OtherSlot != null)
                lines.Add(string.Format(Properties.Resources.super_space_slot, space.OtherSlot,
                    Helper.byte2AUnit(space.OtherSlotBytes)));
            if (space.CowBytes > 0)
                lines.Add(string.Format(Properties.Resources.super_space_cow, Helper.byte2AUnit(space.CowBytes)));
            if (space.UnslottedBytes > 0)
                lines.Add(string.Format(Properties.Resources.super_space_unslotted, Helper.byte2AUnit(space.UnslottedBytes)));
            MainWindow.THIS.fastboot_super_text.Text = string.Join("\n", lines);
        }

        /// <summary>
        /// Before writing to logical partitions in fastbootd: when super is known to be too
        /// small for the images, says by how much and what could make room, and asks.
        /// True to go ahead.
        /// </summary>
        static bool superSpaceAllowed(IEnumerable<KeyValuePair<string, long>> writes, IEnumerable<string> created = null)
        {
            if (fastbootData == null || !fastbootData.fastbootd)
                return true;

            SuperSpace space = SuperSpace.From(fastbootData);
            SuperFit fit = space.Check(writes, created);
            appendLog("super space: " + (space.Known ? Helper.byte2AUnit(space.Free) + " free" : "size unknown")
                + ", writes need " + Helper.byte2AUnit(fit.Needed) + (fit.Fits ? "" : " (does not fit)"));
            if (fit.Fits)
                return true;

            List<string> ways = new List<string>();
            if (space.OtherSlotPartitions.Count > 0)
                ways.Add(string.Format(Properties.Resources.super_full_other_slot, space.OtherSlot,
                    string.Join(", ", space.OtherSlotPartitions.Take(6)
                        .Select(p => p.Name + " (" + Helper.byte2AUnit(p.Size) + ")"))));
            if (space.CowPartitions.Count > 0 || fastbootData.HasPendingUpdate)
                ways.Add(Properties.Resources.super_full_cow);
            if (ways.Count == 0)
                ways.Add(Properties.Resources.super_full_nothing);

            string text = string.Format(Properties.Resources.super_full,
                    Helper.byte2AUnit(fit.Needed), string.Join(", ", fit.Growing), Helper.byte2AUnit(fit.Free))
                + "\n\n" + string.Join("\n\n", ways.Select(w => "•  " + w))
                + "\n\n" + Properties.Resources.super_full_ask;
            return Helper.confirm(text, Properties.Resources.super_full_title, true);
        }

        /// <summary>
        /// The USB check: looks at what Windows sees on USB and explains why a phone may be
        /// missing from the list (no driver, a broken driver, a download mode, Android mode).
        /// </summary>
        static void checkUsb()
        {
            MainWindow.THIS.fastboot_usb_check.IsEnabled = false;
            IReadOnlyList<UsbFinding> findings = null;
            Exception failure = null;

            Helper.offloadAndRun(new Action(delegate
            {
                try
                {
                    findings = UsbDiagnosis.Diagnose(UsbScan.Read());
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }), new Action(delegate
            {
                MainWindow.THIS.fastboot_usb_check.IsEnabled = true;
                if (failure != null)
                {
                    appendLog("usb check failed: " + failure.Message);
                    ThemedDialog.Show(failure.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                showUsbFindings(findings);
            }));
        }

        static void showUsbFindings(IReadOnlyList<UsbFinding> findings)
        {
            List<string> lines = new List<string>();
            bool driverTrouble = false;
            bool adbReady = false;

            foreach (UsbFinding finding in findings)
            {
                appendLog("usb: " + finding.Kind + ", " + finding.Device.Name + " " + finding.UsbId
                    + ", code " + finding.Device.ErrorCode);
                string device = finding.Device.Name + (finding.UsbId != null ? "  (" + finding.UsbId + ")" : "");
                switch (finding.Kind)
                {
                    case UsbFindingKind.MissingDriver:
                        lines.Add(string.Format(Properties.Resources.usb_missing_driver, device));
                        driverTrouble = true;
                        break;
                    case UsbFindingKind.DriverProblem:
                        lines.Add(string.Format(Properties.Resources.usb_driver_problem, device, finding.Device.ErrorCode));
                        driverTrouble = true;
                        break;
                    case UsbFindingKind.QualcommEdl:
                        lines.Add(string.Format(Properties.Resources.usb_edl, device));
                        break;
                    case UsbFindingKind.MediaTekDownload:
                        lines.Add(string.Format(Properties.Resources.usb_mediatek, device));
                        break;
                    case UsbFindingKind.UnisocDownload:
                        lines.Add(string.Format(Properties.Resources.usb_unisoc, device));
                        break;
                    case UsbFindingKind.SamsungDownload:
                        lines.Add(string.Format(Properties.Resources.usb_samsung, device));
                        break;
                    case UsbFindingKind.FastbootReady:
                        lines.Add(string.Format(Properties.Resources.usb_fastboot_ready, device));
                        break;
                    case UsbFindingKind.AdbReady:
                        lines.Add(string.Format(Properties.Resources.usb_adb_ready, device));
                        adbReady = true;
                        break;
                }
            }

            if (lines.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.usb_nothing, Properties.Resources.usb_check_title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string text = string.Join("\n\n", lines.Select(line => "•  " + line));

            if (driverTrouble)
            {
                if (ThemedDialog.Show(text + "\n\n" + Properties.Resources.usb_driver_howto
                        + "\n\n" + Properties.Resources.usb_open_device_manager,
                        Properties.Resources.usb_check_title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
                    }
                    catch (Exception e)
                    {
                        ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                return;
            }

            if (adbReady)
            {
                if (!Helper.confirm(text + "\n\n" + Properties.Resources.usb_reboot_bootloader_ask, Properties.Resources.usb_check_title))
                    return;

                string problem = null;
                Helper.offloadAndRun(new Action(delegate
                {
                    try
                    {
                        Adb.Result result = Adb.Run("reboot bootloader", Adb.ShortCommand);
                        if (!result.Succeeded)
                            problem = result.Problem;
                    }
                    catch (Exception e)
                    {
                        problem = e.Message;
                    }
                }), new Action(delegate
                {
                    appendLog("adb reboot bootloader: " + (problem ?? "ok"));
                    if (problem != null)
                        ThemedDialog.Show(problem, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                }));
                return;
            }

            ThemedDialog.Show(text, Properties.Resources.usb_check_title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        static void change_page()
        {
            bool showActions = cur_status == FastbootStatus.show_actions;

            if (showActions)
                load_fastboot_vars();
            else
                MainWindow.THIS.fastboot_mode_badge.Visibility = Visibility.Collapsed;

            MainWindow.THIS.fastboot_devices_page.Visibility = showActions ? Visibility.Collapsed : Visibility.Visible;
            MainWindow.THIS.fastboot_actions_page.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            MainWindow.THIS.fastboot_device_chip.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            MainWindow.THIS.fastboot_remove.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            MainWindow.THIS.flash_target_device.Text = showActions && cur_serial != null
                ? string.Format(Properties.Resources.flash_target, cur_serial)
                : Properties.Resources.flash_target_none;
        }

        class StepCmdRunnerParam
        {
            public string cmd;
            public int step_count;
            public bool show_dialog_on_done;
            public bool skip_var_refresh;

            /// <summary>
            /// Captured on the UI thread when the command is created. Reboot clears cur_serial
            /// straight after starting the worker, so reading it there could send the command
            /// without -s, and fastboot would then act on whichever device it found first.
            /// </summary>
            public string serial;

            /// <summary>Control commands get a limit; anything that writes is left to finish.</summary>
            public TimeSpan timeout;

            /// <summary>Shown instead of "operation completed" when set.</summary>
            public string done_message;

            /// <summary>Back to the device list once the command succeeded (the phone left fastboot).</summary>
            public bool leave_after;

            public StepCmdRunnerParam(string cmd, int step_count, bool hint_on_done, bool skip_var_refresh = false)
            {
                this.timeout = cmd.StartsWith("reboot") || cmd.StartsWith("set_active") || cmd.StartsWith("snapshot-update")
                    ? Fastboot.LongCommand
                    : Fastboot.NoLimit;
                this.cmd = cmd;
                this.step_count = step_count;
                this.show_dialog_on_done = hint_on_done;
                this.skip_var_refresh = skip_var_refresh;
                this.serial = cur_serial;
            }
        }

        static void runStep(StepCmdRunnerParam param)
        {
            // Locked here, on the click, so a second click cannot start the command again
            // before the worker gets going.
            action_lock();
            if (param.step_count <= 0)
                MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = true;

            Thread worker = new Thread(new ParameterizedThreadStart(step_cmd_runner_err));
            worker.IsBackground = true;
            worker.Start(param);
        }

        static void step_cmd_runner_err(object raw_param)
        {
            StepCmdRunnerParam param = (StepCmdRunnerParam)raw_param;

            appendLog("> fastboot " + param.cmd);
            string failure = null;

            try
            {
                using (Fastboot fastboot = new Fastboot(param.serial, param.cmd, param.timeout))
                {
                    int count = 0;
                    while (true)
                    {
                        string err = fastboot.stderr.ReadLine();

                        if (err == null)
                            break;

                        appendLog(err);

                        if (param.step_count > 0)
                            MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                count++;
                                int percent = Math.Min(100, count * 100 / param.step_count);
                                MainWindow.THIS.fastboot_progress_bar.Value = percent;
                                Helper.TaskbarItemHelper.update(percent);
                            }));
                    }

                    int? exitCode = fastboot.WaitForExit();
                    if (exitCode != 0)
                    {
                        failure = "fastboot " + param.cmd + ": " + (exitCode == null
                            ? "did not finish within " + param.timeout.TotalMinutes + " minutes and was stopped"
                            : "exited with code " + exitCode);
                        appendLog(failure);
                    }
                }
            }
            catch (Exception e)
            {
                failure = e.Message;
                appendLog("fastboot " + param.cmd + " failed: " + e.Message);
            }

            MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
            {
                MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
                action_unlock();

                if (failure != null)
                {
                    ThemedDialog.Show(failure, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (param.leave_after && cur_serial == param.serial)
                    leaveDevice();
                else if (!param.skip_var_refresh && cur_serial != null && cur_serial == param.serial)
                    load_fastboot_vars();
                if (param.show_dialog_on_done)
                    ThemedDialog.Done(param.done_message ?? Properties.Resources.operation_completed);
            }));
        }

        /// <summary>
        /// Checks the target slot's boot state and, in fastbootd, its logical partitions, then
        /// asks (or refuses) accordingly. True when the switch may go ahead.
        /// </summary>
        static bool slotSwitchAllowed(string target)
        {
            SlotSwitchCheck check = SlotSwitchCheck.Evaluate(fastbootData, target);
            appendLog("slot " + target + " check: " + check.Level
                + (check.Findings.Count > 0 ? " (" + string.Join(", ", check.Findings.Select(f => f.Reason)) + ")" : ""));

            List<string> lines = new List<string>();
            foreach (SlotCheckFinding finding in check.Findings.Where(f => f.Level != SlotCheckLevel.Ok))
            {
                switch (finding.Reason)
                {
                    case SlotCheckReason.NeverBooted:
                        lines.Add(string.Format(Properties.Resources.slot_reason_never_booted, target));
                        break;
                    case SlotCheckReason.Unbootable:
                        lines.Add(string.Format(Properties.Resources.slot_reason_unbootable, target));
                        break;
                    case SlotCheckReason.NoRetriesLeft:
                        lines.Add(string.Format(Properties.Resources.slot_reason_no_retries, target));
                        break;
                    case SlotCheckReason.EmptyPartitions:
                        lines.Add(string.Format(Properties.Resources.slot_reason_empty, target, string.Join(", ", finding.Partitions)));
                        break;
                }
            }
            string details = string.Join("\n\n", lines.Select(l => "•  " + l));

            switch (check.Level)
            {
                case SlotCheckLevel.Block:
                    ThemedDialog.Show(string.Format(Properties.Resources.slot_switch_blocked, target) + "\n\n" + details,
                        Properties.Resources.slot_switch_title, MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                case SlotCheckLevel.Danger:
                    return Helper.confirm(string.Format(Properties.Resources.slot_switch_danger, target) + "\n\n" + details,
                        Properties.Resources.slot_switch_title, true);
                case SlotCheckLevel.Warn:
                    return Helper.confirm(string.Format(Properties.Resources.slot_switch_warn, target) + "\n\n" + details,
                        Properties.Resources.slot_switch_title);
                default:
                    return true;
            }
        }

        /// <summary>
        /// "fastboot boot": starts an image once from memory. Only an image with a kernel can
        /// start, and only the bootloader (not fastbootd) can do it.
        /// </summary>
        static void bootOnce(string path)
        {
            BootImageHeader header;
            try
            {
                header = BootImageHeader.Read(path);
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string refusal = header.Kind == BootImageKind.NoKernel ? Properties.Resources.boot_once_no_kernel
                : header.Kind == BootImageKind.VendorBoot ? Properties.Resources.boot_once_vendor_boot
                : header.Kind == BootImageKind.NotBootImage ? Properties.Resources.boot_once_not_boot
                : null;
            if (refusal != null)
            {
                appendLog("boot once refused for " + path + ": " + header.Kind);
                ThemedDialog.Show(refusal, Properties.Resources.fastboot_boot_once_title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (fastbootData.fastbootd)
            {
                if (Helper.confirm(Properties.Resources.boot_once_fastbootd, Properties.Resources.fastboot_boot_once_title))
                    runStep(new StepCmdRunnerParam("reboot bootloader", 2, false));
                return;
            }

            runStep(new StepCmdRunnerParam("boot \"" + path + "\"", 0, true, true)
            {
                done_message = Properties.Resources.boot_once_started,
                leave_after = true,
            });
        }

        static bool singlePartitionCheck()
        {
            if (MainWindow.THIS.fastboot_partition_list.SelectedItems.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.fastboot_target_partition_not_selected);
                return true;
            }

            if (MainWindow.THIS.fastboot_partition_list.SelectedItems.Count > 1)
            {
                ThemedDialog.Show(Properties.Resources.fastboot_not_support_multiselect);
                return true;
            }

            return false;
        }

        static bool logicalCheck()
        {
            bool? ret = null;
            fastbootData.partition_is_logical.TryGetValue(
                ((fastboot_partition_row)
                MainWindow.THIS.fastboot_partition_list.SelectedItem).name, out ret);
            if (ret == null || ret == false)
            {
                ThemedDialog.Show(Properties.Resources.fastboot_only_logical);
                return true;
            }

            return false;
        }

        static bool vabStagingCheck()
        {
            if (fastbootData.HasPendingUpdate)
            {
                bool proceed = Helper.confirm(
                    Properties.Resources.fastboot_vab_staging_str1 + "\n" +
                    Properties.Resources.fastboot_vab_staging_str2 + "\n" +
                    Properties.Resources.fastboot_vab_staging_str3,
                    Properties.Resources.fastboot_vab_staging_str0, true);

                if (!proceed)
                {
                    return true;
                }
            }

            if (fastbootData.HasCowPartitions)
            {
                bool proceed = Helper.confirm(
                    Properties.Resources.fastboot_cow_exist_str1 + "\n" +
                    Properties.Resources.fastboot_cow_exist_str2 + "\n" +
                    Properties.Resources.fastboot_cow_exist_str3,
                    Properties.Resources.fastboot_cow_exist_str0, true);

                if (!proceed)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Opens a payload, makes sure the device can take it, then hands over to the flasher.
        /// </summary>
        static void openPayloadThenFlash(string path, string serial)
        {
            PayloadFile opened = null;
            Exception failure = null;

            action_lock();
            MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = true;

            Helper.offloadAndRun(
                new Action(delegate
                {
                    try
                    {
                        opened = PayloadFile.Open(path, PAYLOAD_TMP);
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                }),
                new Action(delegate
                {
                    MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;

                    if (failure != null)
                    {
                        action_unlock();
                        ThemedDialog.Show(Properties.Resources.payload_unsupported_format
                            + "\n" + failure.Message);
                        return;
                    }

                    MissingPartitions missing = findMissingPartitions(opened);
                    if (missing.Unknown.Count > 0)
                    {
                        opened.Dispose();
                        action_unlock();
                        string hint = fastbootData.fastbootd
                            ? "\n" + Properties.Resources.fastboot_unknown_partition_str1
                            : "\n" + Properties.Resources.fastboot_unknown_partition_str2;
                        ThemedDialog.Show(Properties.Resources.fastboot_unknown_partition_str0
                            + "\n" + string.Join(" ", missing.Unknown) + hint);
                        return;
                    }

                    // Logical partitions the update adds (or the phone lost) are created first.
                    if (missing.Create.Count > 0)
                    {
                        appendLog("missing logical partitions: " + string.Join(", ", missing.Create));
                        if (!Helper.confirm(string.Format(Properties.Resources.create_missing,
                                string.Join(", ", missing.Create)), Properties.Resources.create_missing_title))
                        {
                            opened.Dispose();
                            action_unlock();
                            return;
                        }
                    }

                    List<string> blocked = opened.Partitions
                        .Where(part => !part.CanExtract)
                        .Select(part => part.Name)
                        .ToList();

                    if (blocked.Count > 0)
                    {
                        opened.Dispose();
                        action_unlock();
                        ThemedDialog.Show(Properties.Resources.payload_incremental_warning
                            + "\n\n" + string.Join(", ", blocked));
                        return;
                    }

                    List<string> unverifiable = opened.Partitions
                        .Where(part => string.IsNullOrEmpty(part.ExpectedSha256))
                        .Select(part => part.Name)
                        .ToList();

                    if (unverifiable.Count > 0 && !Helper.confirm(
                            string.Format(Properties.Resources.flash_unverified, string.Join(", ", unverifiable)),
                            Properties.Resources.confirm_title, true))
                    {
                        opened.Dispose();
                        action_unlock();
                        return;
                    }

                    // The device must still be the one the user picked when they clicked flash.
                    if (serial == null || serial != cur_serial || fastbootData == null)
                    {
                        opened.Dispose();
                        action_unlock();
                        ThemedDialog.Show(Properties.Resources.flash_no_device);
                        return;
                    }

                    // In fastbootd each logical partition is resized to its image as it is
                    // written; make sure super can take them, in the order they are written.
                    Dictionary<string, string> createdAs = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < missing.Create.Count; i++)
                        createdAs[missing.CreateFrom[i]] = missing.Create[i];
                    List<KeyValuePair<string, long>> writes = opened.Partitions
                        .Select(part => new KeyValuePair<string, long>(
                            createdAs.ContainsKey(part.Name) ? createdAs[part.Name] : part.Name, part.UnpackedSize))
                        .ToList();
                    if (!superSpaceAllowed(writes, missing.Create))
                    {
                        opened.Dispose();
                        action_unlock();
                        return;
                    }

                    flashPayload(opened, serial, missing.Create);
                }));
        }

        /// <summary>
        /// The partitions of the payload the phone does not have, split into those that can
        /// be created in super and those that cannot. Nothing when the user chose to ignore them.
        /// </summary>
        static MissingPartitions findMissingPartitions(PayloadFile payload)
        {
            if (MainWindow.THIS.ignore_unknown_part.IsChecked == true)
                return MissingPartitions.Find(fastbootData, new string[0], new string[0]);

            return MissingPartitions.Find(fastbootData, payload.Partitions.Select(part => part.Name),
                payload.LogicalPartitionNames());
        }

        /// <summary>
        /// "create-logical-partition NAME 0" for each partition; fastbootd then sizes each one to
        /// the image written to it. Returns the error, or null when all were created.
        /// </summary>
        static string createLogicalPartitions(string serial, IReadOnlyList<string> names, CancellationToken cancel)
        {
            foreach (string name in names)
            {
                cancel.ThrowIfCancellationRequested();
                appendLog("Creating logical partition " + name);
                int? exitCode;
                using (Fastboot fastboot = new Fastboot(serial,
                    "create-logical-partition \"" + name + "\" 0", Fastboot.LongCommand))
                {
                    while (true)
                    {
                        string line = fastboot.stderr.ReadLine();
                        if (line == null)
                            break;
                        appendLog(line);
                    }
                    exitCode = fastboot.WaitForExit();
                }
                if (exitCode != 0)
                    return name + ": create-logical-partition " + (exitCode == null
                        ? "did not finish in time"
                        : "exited with code " + exitCode);
            }
            return null;
        }

        /// <summary>
        /// Extracts the whole payload first, in parallel, and only then writes it to the device.
        /// Nothing reaches the device until every image is present and its hash checks out, so a
        /// payload that turns out to be damaged cannot leave the phone half written. Each image
        /// is deleted as soon as it is flashed to keep the staging directory small.
        /// </summary>
        static void flashPayload(PayloadFile payload, string serial, IReadOnlyList<string> create)
        {
            if (serial == null)
                throw new ArgumentNullException(nameof(serial));

            string staging = Path.Combine(PAYLOAD_TMP, "stage-" + Guid.NewGuid().ToString("N"));

            List<PayloadPartitionInfo> parts = payload.Partitions.ToList();
            long totalBytes = Math.Max(1, parts.Sum(part => part.UnpackedSize));

            ExtractionOptions options = new ExtractionOptions
            {
                VerifyOperationHashes = true,
                VerifyImageHash = true,
                IgnoreUnsupportedOperations = false,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            };

            // The Flash page's queue: one row per partition, updated as work moves along.
            System.Collections.ObjectModel.ObservableCollection<FlashRow> rows =
                new System.Collections.ObjectModel.ObservableCollection<FlashRow>();
            Dictionary<string, FlashRow> rowByName = new Dictionary<string, FlashRow>(StringComparer.Ordinal);
            foreach (PayloadPartitionInfo part in parts)
            {
                FlashRow row = new FlashRow(part.Name, Helper.byte2AUnit(part.UnpackedSize));
                rows.Add(row);
                rowByName[part.Name] = row;
            }

            MainWindow.THIS.flash_queue.ItemsSource = rows;
            MainWindow.THIS.flash_file_name.Text = Path.GetFileName(payload.Source.ContainerPath);
            MainWindow.THIS.flash_file_detail.Text = string.Format(Properties.Resources.flash_file_detail,
                parts.Count, Helper.byte2AUnit(totalBytes), payload.Source.FromZip ? "OTA zip" : "payload.bin");
            MainWindow.THIS.flash_count.Text = "0 / " + parts.Count;
            MainWindow.THIS.flash_overall_progress.Value = 0;
            MainWindow.THIS.flash_log.Clear();
            MainWindow.forgetLength(MainWindow.THIS.flash_log);
            setPhase(Properties.Resources.flash_phase_extract, "Accent");
            MainWindow.THIS.main_tabs.SelectedItem = MainWindow.THIS.flash_tab;

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            System.Windows.Threading.DispatcherTimer ticker = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            ticker.Tick += delegate { MainWindow.THIS.flash_elapsed.Text = clock.Elapsed.ToString(@"mm\:ss"); };
            ticker.Start();

            // Extraction counts for the first half of the bar, flashing for the second. The bar
            // only moves forward: the extraction phase finishes before the flashing phase reports.
            int lastPercent = -1;

            Action<int> showPercent = new Action<int>(delegate (int percent)
            {
                if (percent <= lastPercent)
                    return;
                lastPercent = percent;

                MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                {
                    MainWindow.THIS.fastboot_progress_bar.Value = percent;
                    MainWindow.THIS.flash_overall_progress.Value = percent;
                    Helper.TaskbarItemHelper.update(percent);
                }));
            });

            ProgressAggregator progress = new ProgressAggregator(totalBytes,
                delegate (int percent) { showPercent(percent / 2); },
                delegate (string partition, int percent)
                {
                    FlashRow row;
                    if (!rowByName.TryGetValue(partition, out row))
                        return;
                    MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        row.Current = FlashRow.Stage.Extracting;
                        row.Progress = percent / 2.0;
                    }));
                });

            Action<string, FlashRow.Stage, double?> updateRow = new Action<string, FlashRow.Stage, double?>(
                delegate (string name, FlashRow.Stage stage, double? value)
                {
                    FlashRow row;
                    if (!rowByName.TryGetValue(name, out row))
                        return;
                    MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        row.Current = stage;
                        if (value != null)
                            row.Progress = value.Value;
                    }));
                });

            // The lock taken by openPayloadThenFlash carries over and is released when this ends.
            flashing = true;

            CancellationTokenSource cancel = new CancellationTokenSource();
            flashCancel = cancel;

            Thread worker = new Thread(new ThreadStart(delegate
            {
                string error = null;
                int flashed = 0;

                try
                {
                    Directory.CreateDirectory(staging);

                    appendLog("Extracting " + parts.Count + " partitions with "
                        + options.MaxDegreeOfParallelism + " workers");

                    ExtractionReport report = PayloadExtractor.ExtractAll(
                        payload, staging, parts.Select(part => part.Name), options, progress,
                        cancel.Token);

                    foreach (PartitionResult result in report.Results)
                    {
                        if (result.Succeeded)
                            updateRow(result.Partition, FlashRow.Stage.Verified, 50.0);
                        else
                            updateRow(result.Partition, FlashRow.Stage.Failed, null);
                    }

                    if (!report.AllSucceeded)
                    {
                        error = string.Join("\n", report.Results
                            .Where(result => !result.Succeeded)
                            .Select(result => result.Partition + ": " + result.Error));
                        foreach (PartitionResult result in report.Results.Where(r => r.Succeeded))
                            updateRow(result.Partition, FlashRow.Stage.NotWritten, null);
                    }
                    else
                    {
                        appendLog("Extracted " + Helper.byte2AUnit(report.TotalBytes)
                            + " in " + report.Elapsed.TotalSeconds.ToString("F1") + " s");

                        MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            setPhase(Properties.Resources.flash_phase_flash, "Accent");
                        }));

                        long flashedBytes = 0;
                        List<PartitionResult> results = report.Results.ToList();

                        // Only now, with every image extracted and verified, is super touched.
                        error = createLogicalPartitions(serial, create, cancel.Token);
                        if (error != null)
                        {
                            appendLog(error);
                            foreach (PartitionResult result in results)
                                updateRow(result.Partition, FlashRow.Stage.NotWritten, null);
                        }

                        for (int i = 0; error == null && i < results.Count; i++)
                        {
                            PartitionResult result = results[i];
                            appendLog("Flashing " + result.Partition);
                            updateRow(result.Partition, FlashRow.Stage.Flashing, 75.0);

                            int? exitCode;
                            cancel.Token.ThrowIfCancellationRequested();
                            using (Fastboot fastboot = new Fastboot(serial,
                                "flash \"" + result.Partition + "\" \"" + result.OutputPath + "\"", Fastboot.NoLimit))
                            {
                                activeFlash = fastboot;
                                // abortFlash may have looked for a process just before this one started.
                                if (cancel.IsCancellationRequested)
                                    fastboot.Abort();
                                while (true)
                                {
                                    string line = fastboot.stderr.ReadLine();
                                    if (line == null)
                                        break;
                                    appendLog(line);
                                }

                                exitCode = fastboot.WaitForExit();
                                activeFlash = null;
                            }

                            // Without this the dialog reported success even when every write failed.
                            if (exitCode != 0)
                            {
                                error = result.Partition + ": fastboot " + (exitCode == null
                                    ? "was stopped before it finished"
                                    : "exited with code " + exitCode);
                                appendLog(error);
                                updateRow(result.Partition, FlashRow.Stage.Failed, null);
                                for (int rest = i + 1; rest < results.Count; rest++)
                                    updateRow(results[rest].Partition, FlashRow.Stage.NotWritten, null);
                                break;
                            }

                            flashed++;
                            flashedBytes += result.Size;
                            updateRow(result.Partition, FlashRow.Stage.Flashed, 100.0);
                            showPercent(50 + (int)(flashedBytes * 50 / totalBytes));

                            int written = flashed;
                            MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                MainWindow.THIS.flash_count.Text = written + " / " + parts.Count;
                            }));

                            try
                            {
                                File.Delete(result.OutputPath);
                            }
                            catch (IOException)
                            {
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                    appendLog("flash failed: " + e.Message);
                }
                finally
                {
                    payload.Dispose();
                    try
                    {
                        if (Directory.Exists(staging))
                            Directory.Delete(staging, true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                string summary = error;
                int flashedCount = flashed;

                MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                {
                    flashing = false;
                    flashWorker = null;
                    flashCancel = null;
                    ticker.Stop();
                    clock.Stop();
                    MainWindow.THIS.flash_elapsed.Text = clock.Elapsed.ToString(@"mm\:ss");
                    action_unlock();
                    if (cur_serial == serial)
                        load_fastboot_vars();

                    if (summary == null)
                    {
                        setPhase(Properties.Resources.flash_phase_done, "Ok");
                        MainWindow.THIS.flash_overall_progress.Value = 100;
                        ThemedDialog.Done(Properties.Resources.operation_completed);
                        return;
                    }

                    setPhase(Properties.Resources.flash_phase_failed, "Danger");
                    Helper.TaskbarItemHelper.error();
                    ThemedDialog.Show(Properties.Resources.payload_error_occur
                        + "\n\n" + summary
                        + "\n\n" + string.Format(Properties.Resources.flash_written_count, flashedCount, parts.Count));
                }));
            }));

            worker.IsBackground = true;
            flashWorker = worker;
            worker.Start();
        }

        static volatile Fastboot activeFlash;
        static volatile CancellationTokenSource flashCancel;
        static volatile Thread flashWorker;

        /// <summary>
        /// Called when the window closes mid-flash, after the user agreed to stop. Stops the
        /// extraction and the fastboot process writing the current image, then waits for the
        /// worker to remove its staging images, so neither a stray fastboot.exe nor gigabytes
        /// of images outlive the app.
        /// </summary>
        public static void abortFlash()
        {
            CancellationTokenSource cancel = flashCancel;
            if (cancel != null)
                cancel.Cancel();

            Fastboot running = activeFlash;
            if (running != null)
                running.Abort();

            Thread worker = flashWorker;
            if (worker != null)
                worker.Join(TimeSpan.FromSeconds(10));
        }

        static void setPhase(string text, string brushKey)
        {
            MainWindow.THIS.flash_phase.Text = text;
            MainWindow.THIS.flash_phase.Foreground = Palette.Get(brushKey);
        }

        static void openSelectedDevice()
        {
            if (MainWindow.THIS.fastboot_devices_list.SelectedItems.Count != 1)
                return;

            fastboot_devices_row cur = (fastboot_devices_row)MainWindow.THIS.fastboot_devices_list.SelectedItem;
            cur_serial = cur.serial;
            if (!checkCurDevExist())
                return;
            cur_status = FastbootStatus.show_actions;
            MainWindow.THIS.fastboot_cur_device.Content = cur.serial;
            appendLog("Opened device " + cur.serial + " (" + cur.name + ")");
            change_page();
        }

        public static void init()
        {
            devices = new List<fastboot_devices_row>();
            cur_status = FastbootStatus.show_devices;
            change_page();

            // Background, so it never keeps the process alive after the window closes.
            Thread refresher = new Thread(new ThreadStart(devicesListRefresher));
            refresher.IsBackground = true;
            refresher.Start();
            MainWindow.THIS.main_tabs.Tag = string.Format(Properties.Resources.rail_devices, 0);

            MainWindow.THIS.fastboot_usb_check.Click += delegate
            {
                checkUsb();
            };

            MainWindow.THIS.fastboot_devices_list.MouseDoubleClick += delegate
            {
                openSelectedDevice();
            };

            // Keyboard users (and UI automation) open a device with Enter.
            MainWindow.THIS.fastboot_devices_list.KeyDown += delegate (object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key != System.Windows.Input.Key.Enter)
                    return;
                e.Handled = true;
                openSelectedDevice();
            };

            MainWindow.THIS.fastboot_remove.Click += delegate
            {
                leaveDevice();
            };

            MainWindow.THIS.fastboot_reboot_d.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (fastbootData.fastbootd)
                {
                    runStep(new StepCmdRunnerParam("reboot bootloader", 2, false));
                }
                else
                {
                    runStep(new StepCmdRunnerParam("reboot fastboot", 3, false));
                }
            };

            MainWindow.THIS.fastboot_reboot_system.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                runStep(new StepCmdRunnerParam("reboot", 0, false, true));

                leaveDevice();
            };

            MainWindow.THIS.fastboot_reboot_recovery.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                runStep(new StepCmdRunnerParam("reboot recovery", 0, false, true));

                leaveDevice();
            };

            MainWindow.THIS.fastboot_ab_switch.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                string target = fastbootData.current_slot == "a" ? "b" : fastbootData.current_slot == "b" ? "a" : null;
                if (target == null)
                {
                    ThemedDialog.Show(Properties.Resources.operation_not_supported);
                    return;
                }
                if (!slotSwitchAllowed(target))
                    return;
                runStep(new StepCmdRunnerParam("set_active " + target, 2, false));
            };

            MainWindow.THIS.fastboot_boot_once.Click += delegate
            {
                if (!checkCurDevExist())
                    return;
                Helper.fileSelect(new Helper.PathSelectCallback(bootOnce), "Boot / recovery images|*.img;*.bin|All files|*.*");
            };

            // Cancel a staged Virtual A/B update.
            MainWindow.THIS.fastboot_cancel_update.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                runStep(new StepCmdRunnerParam("snapshot-update cancel", 2, true));
            };

            MainWindow.THIS.fastboot_flash.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (singlePartitionCheck())
                    return;

                if (vabStagingCheck())
                    return;

                string target = ((fastboot_partition_row)MainWindow.THIS.fastboot_partition_list.SelectedItem).name;

                if (PartitionSafety.IsBrickRisk(target) && !ThemedDialog.ConfirmTyped(
                        string.Format(Properties.Resources.confirm_critical_flash, target), Properties.Resources.critical_title, target))
                {
                    appendLog("flash of critical partition " + target + " not confirmed");
                    return;
                }

                Helper.fileSelect(new Helper.PathSelectCallback(delegate (string path)
                {
                    // fastbootd resizes a logical partition to its image: check super has room.
                    bool? isLogical;
                    if (fastbootData.partition_is_logical.TryGetValue(target, out isLogical) && isLogical == true)
                    {
                        long imageSize;
                        try
                        {
                            imageSize = SuperSpace.WrittenSize(path);
                        }
                        catch (Exception e)
                        {
                            ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                        if (!superSpaceAllowed(new[] { new KeyValuePair<string, long>(target, imageSize) }))
                            return;
                    }

                    string ext_arg = "";

                    if (target == "vbmeta" || target == "vbmeta_" + fastbootData.current_slot
                    || target == "vbmeta_a" || target == "vbmeta_b")
                    {
                        if (Helper.confirm(Properties.Resources.fastboot_vbmeta_disable_verify,
                                Properties.Resources.fastboot_vbmeta_disable_verify_title))
                        {
                            ext_arg += "--disable-verity --disable-verification";
                        }
                    }
                    runStep(new StepCmdRunnerParam("flash " + ext_arg + " \"" + target + "\" \"" + path + "\"", -1, true));
                }), "Image File|*.img;*.image");
            };

            MainWindow.THIS.fastboot_erase.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (singlePartitionCheck())
                    return;

                string target = ((fastboot_partition_row)MainWindow.THIS.fastboot_partition_list.SelectedItem).name;

                bool confirmed = PartitionSafety.IsBrickRisk(target)
                    ? ThemedDialog.ConfirmTyped(string.Format(Properties.Resources.confirm_critical_erase, target),
                        Properties.Resources.critical_title, target)
                    : Helper.confirm(string.Format(Properties.Resources.confirm_erase, target), Properties.Resources.confirm_title, true);
                if (!confirmed)
                    return;

                runStep(new StepCmdRunnerParam("erase \"" + target + "\"", 2, false));
            };

            MainWindow.THIS.fastboot_partition_list.SelectionChanged += delegate
            {
                MainWindow.THIS.fastboot_flash.IsEnabled = true;
                MainWindow.THIS.fastboot_erase.IsEnabled = true;
                MainWindow.THIS.fastboot_logical_delete.IsEnabled = true;
                MainWindow.THIS.fastboot_logical_resize.IsEnabled = true;

                if (MainWindow.THIS.fastboot_partition_list.SelectedItems.Count > 1)
                {
                    MainWindow.THIS.fastboot_flash.IsEnabled = false;
                    MainWindow.THIS.fastboot_erase.IsEnabled = false;
                    MainWindow.THIS.fastboot_logical_delete.IsEnabled = false;
                    MainWindow.THIS.fastboot_logical_resize.IsEnabled = false;
                    return;
                }

                if (fastbootData == null)
                    return;

                if (!fastbootData.fastbootd)
                {
                    MainWindow.THIS.fastboot_logical_delete.IsEnabled = false;
                    MainWindow.THIS.fastboot_logical_resize.IsEnabled = false;
                    return;
                }

                bool? ret = null;

                if (MainWindow.THIS.fastboot_partition_list.SelectedItem == null)
                {
                    MainWindow.THIS.fastboot_logical_delete.IsEnabled = true;
                    MainWindow.THIS.fastboot_logical_resize.IsEnabled = true;
                    return;
                }
                fastbootData.partition_is_logical.TryGetValue(
                    ((fastboot_partition_row)
                    MainWindow.THIS.fastboot_partition_list.SelectedItem).name, out ret);
                if (ret == null || ret == false)
                {
                    MainWindow.THIS.fastboot_logical_delete.IsEnabled = false;
                    MainWindow.THIS.fastboot_logical_resize.IsEnabled = false;
                }
            };

            MainWindow.THIS.fastboot_logical_delete.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (singlePartitionCheck() || logicalCheck())
                    return;

                string target = ((fastboot_partition_row)MainWindow.THIS.fastboot_partition_list.SelectedItem).name;

                if (!Helper.confirm(string.Format(Properties.Resources.confirm_delete, target), Properties.Resources.confirm_title, true))
                    return;

                runStep(new StepCmdRunnerParam("delete-logical-partition \"" + target + "\"", 2, false));
            };

            MainWindow.THIS.fastboot_logical_create.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                new FastbootActionWindow(FastbootActionWindow.StartType.CREATE, "", 0,
                    delegate (string name, ulong size)
                   {
                       runStep(new StepCmdRunnerParam(
                            "create-logical-partition \"" + name + "\" \"" + size.ToString() + "\"", 2, false));
                   }).ShowDialog();
            };

            MainWindow.THIS.fastboot_logical_resize.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (singlePartitionCheck() || logicalCheck())
                    return;

                string target = ((fastboot_partition_row)MainWindow.THIS.fastboot_partition_list.SelectedItem).name;

                new FastbootActionWindow(FastbootActionWindow.StartType.RESIZE, target,
                    fastbootData.partition_size[target],
                    delegate (string name, ulong size)
                    {
                        runStep(new StepCmdRunnerParam(
                             "resize-logical-partition \"" + name + "\" \"" + size.ToString() + "\"", 2, false));
                    }).ShowDialog();
            };

            MainWindow.THIS.fastboot_flash_payload.Click += delegate
            {
                if (cur_status != FastbootStatus.show_actions || cur_serial == null || fastbootData == null)
                {
                    ThemedDialog.Show(Properties.Resources.flash_no_device);
                    MainWindow.THIS.main_tabs.SelectedItem = MainWindow.THIS.device_tab;
                    return;
                }

                if (!checkCurDevExist())
                    return;

                if (vabStagingCheck())
                    return;

                string serial = cur_serial;
                Helper.fileSelect(new Helper.PathSelectCallback(delegate (string path)
                {
                    openPayloadThenFlash(path, serial);
                }), "Payload or OTA package|*.bin;*.zip|All Files|*.*");
            };

            listHelper = new Helper.ListHelper<fastboot_partition_row>(MainWindow.THIS.fastboot_partition_list,
                new Helper.ListHelper<fastboot_partition_row>.Filter(delegate (fastboot_partition_row row)
                {
                    if (MainWindow.THIS.fastboot_partition_name_textbox.Text == "")
                        return true;

                    if (row.name.Contains(MainWindow.THIS.fastboot_partition_name_textbox.Text))
                        return true;
                    return false;
                }));

            MainWindow.THIS.fastboot_partition_name_textbox.TextChanged += delegate
            {
                listHelper.doFilter();
            };
        }
    }
}
