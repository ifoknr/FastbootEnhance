using ChromeosUpdateEngine;
using FastbootEnhance.Core.Payload;
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
            Path.Combine(Path.GetTempPath(), "FastbootEnhance", "flash");

        /// <summary>Generous enough for a multi-gigabyte image over slow USB.</summary>
        static readonly TimeSpan FlashTimeout = TimeSpan.FromMinutes(15);
        static List<fastboot_devices_row> devices;
        static string cur_serial;
        static FastbootData fastbootData;

        static Logger logger;
        static void appendLog(string logs)
        {
            if (logger == null)
                return;

            logger.appendLog(logs);
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
                    if (cur_serial == param[0])
                        return true;
                }
                MessageBox.Show(Properties.Resources.fastboot_device_not_exist);
                cur_status = FastbootStatus.show_devices;
                change_page();
                return false;
            }
        }

        static void devicesListRefresher()
        {
            while (true)
            {
                Thread.Sleep(1000);

                if (cur_status == FastbootStatus.show_actions)
                    continue;

                List<fastboot_devices_row> tmp = new List<fastboot_devices_row>();

                using (Fastboot fastboot = new Fastboot(null, "devices"))
                {
                    while (true)
                    {
                        string line = fastboot.stdout.ReadLine();
                        if (line == null)
                            break;

                        string[] param = line.Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        tmp.Add(new fastboot_devices_row(param[0], param[1]));
                    }
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
                this.value = value;
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

        static void action_lock()
        {
            MainWindow.THIS.fastboot_progress_bar.Value = 0;
            MainWindow.THIS.fastboot_action_bar.Visibility = Visibility.Hidden;
            MainWindow.THIS.fastboot_progress_bar.Visibility = Visibility.Visible;
            MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
            Helper.TaskbarItemHelper.start();
            MainWindow.THIS.fastboot_single_part_op.IsEnabled = false;
            MainWindow.THIS.fastboot_flash_payload.IsEnabled = false;
        }

        static void action_unlock()
        {
            MainWindow.THIS.fastboot_progress_bar.Visibility = Visibility.Hidden;
            MainWindow.THIS.fastboot_action_bar.Visibility = Visibility.Visible;
            Helper.TaskbarItemHelper.stop();
            MainWindow.THIS.fastboot_single_part_op.IsEnabled = true;
            MainWindow.THIS.fastboot_flash_payload.IsEnabled = true;
        }

        static Helper.ListHelper<fastboot_partition_row> listHelper;

        static void load_fastboot_vars()
        {
            //reset
            fastbootData = null;
            listHelper.clear();
            MainWindow.THIS.fastboot_partition_name_textbox.Text = "";
            MainWindow.THIS.fastboot_info_list.Items.Clear();
            action_lock();
            MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = true;

            new Thread(new ThreadStart(delegate
            {
                using (Fastboot fastboot = new Fastboot(cur_serial, "getvar all"))
                {
                    // fastboot bug: Must read stderr first or stdout would be blocked
                    fastbootData = new FastbootData(fastboot.stderr.ReadToEnd());
                }

                MainWindow.THIS.Dispatcher.Invoke(delegate
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

                    MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_device, fastbootData.product));

                    MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_secure_boot,
                        fastbootData.secure ? Properties.Resources.enabled : Properties.Resources.disabled));

                    MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_seamless_update,
                        fastbootData.current_slot != null ? Properties.Resources.yes : Properties.Resources.no));

                    if (fastbootData.current_slot != null)
                        MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_current_slot,
                            fastbootData.current_slot));

                    MainWindow.THIS.fastboot_info_list.Items.Add(new fastboot_info_row(Properties.Resources.fastboot_is_userspace,
                        fastbootData.fastbootd ? Properties.Resources.yes : Properties.Resources.no));

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
                    if (fastbootData.current_slot != null)
                    {
                        MainWindow.THIS.fastboot_ab_switch.Visibility = Visibility.Visible;
                        if (fastbootData.current_slot == "a")
                        {
                            MainWindow.THIS.fastboot_ab_switch.Content = Properties.Resources.fastboot_setactive_b;
                        }
                        else if (fastbootData.current_slot == "b")
                        {
                            MainWindow.THIS.fastboot_ab_switch.Content = Properties.Resources.fastboot_setactive_a;
                        }
                    }
                    else
                    {
                        MainWindow.THIS.fastboot_ab_switch.Visibility = Visibility.Hidden;
                    }

                    // Hide the cancel-update button when there is no staged update to cancel.
                    if (fastbootData.snapshot_update_status == "none")
                    {
                        MainWindow.THIS.fastboot_cancel_update.Visibility = Visibility.Hidden;
                    }

                    MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = false;
                    action_unlock();
                });
            })).Start();
        }

        static void change_page()
        {
            switch (cur_status)
            {
                case FastbootStatus.show_devices:
                    MainWindow.THIS.fastboot_actions_page.Visibility = Visibility.Hidden;
                    MainWindow.THIS.fastboot_devices_page.Visibility = Visibility.Visible;
                    break;
                case FastbootStatus.show_actions:
                    load_fastboot_vars();
                    MainWindow.THIS.fastboot_devices_page.Visibility = Visibility.Hidden;
                    MainWindow.THIS.fastboot_actions_page.Visibility = Visibility.Visible;
                    break;
            }
        }

        class StepCmdRunnerParam
        {
            public string cmd;
            public int step_count;
            public bool show_dialog_on_done;
            public bool skip_var_refresh;
            public StepCmdRunnerParam(string cmd, int step_count, bool hint_on_done, bool skip_var_refresh = false)
            {
                this.cmd = cmd;
                this.step_count = step_count;
                this.show_dialog_on_done = hint_on_done;
                this.skip_var_refresh = skip_var_refresh;
            }
        }

        static void step_cmd_runner_err(object raw_param)
        {
            StepCmdRunnerParam param = (StepCmdRunnerParam)raw_param;

            MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
            {
                action_lock();
                if (param.step_count <= 0)
                    MainWindow.THIS.fastboot_progress_bar.IsIndeterminate = true;
            }));

            using (Fastboot fastboot = new Fastboot(cur_serial, param.cmd))
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
                            MainWindow.THIS.fastboot_progress_bar.Value = ++count * 100 / param.step_count;
                            Helper.TaskbarItemHelper.update(count * 100 / param.step_count);
                        }));
                }
            }

            MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!param.skip_var_refresh)
                    load_fastboot_vars();
                if (param.show_dialog_on_done)
                    MessageBox.Show(Properties.Resources.operation_completed);
            }));
        }

        static bool singlePartitionCheck()
        {
            if (MainWindow.THIS.fastboot_partition_list.SelectedItems.Count == 0)
            {
                MessageBox.Show(Properties.Resources.fastboot_target_partition_not_selected);
                return true;
            }

            if (MainWindow.THIS.fastboot_partition_list.SelectedItems.Count > 1)
            {
                MessageBox.Show(Properties.Resources.fastboot_not_support_multiselect);
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
                MessageBox.Show(Properties.Resources.fastboot_only_logical);
                return true;
            }

            return false;
        }

        static bool vabStagingCheck()
        {
            if (fastbootData.snapshot_update_status != null
                && fastbootData.snapshot_update_status != "none")
            {
                bool proceed = Helper.confirm(
                    Properties.Resources.fastboot_vab_staging_str1 + "\n" +
                    Properties.Resources.fastboot_vab_staging_str2 + "\n" +
                    Properties.Resources.fastboot_vab_staging_str3,
                    Properties.Resources.fastboot_vab_staging_str0);

                if (!proceed)
                {
                    return true;
                }
            }

            bool cow_exist = false;
            foreach (string key in fastbootData.partition_size.Keys)
            {
                if (key.EndsWith("cow"))
                {
                    cow_exist = true;
                    break;
                }
            }

            if (cow_exist)
            {
                bool proceed = Helper.confirm(
                    Properties.Resources.fastboot_cow_exist_str1 + "\n" +
                    Properties.Resources.fastboot_cow_exist_str2 + "\n" +
                    Properties.Resources.fastboot_cow_exist_str3,
                    Properties.Resources.fastboot_cow_exist_str0);

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
        static void openPayloadThenFlash(string path)
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
                        MessageBox.Show(Properties.Resources.payload_unsupported_format
                            + "\n" + failure.Message);
                        return;
                    }

                    string unknown = findUnknownPartitions(opened);
                    if (unknown != null)
                    {
                        opened.Dispose();
                        action_unlock();
                        string hint = fastbootData.fastbootd
                            ? "\n" + Properties.Resources.fastboot_unknown_partition_str1
                            : "\n" + Properties.Resources.fastboot_unknown_partition_str2;
                        MessageBox.Show(Properties.Resources.fastboot_unknown_partition_str0
                            + "\n" + unknown + hint);
                        return;
                    }

                    List<string> blocked = opened.Partitions
                        .Where(part => !part.CanExtract)
                        .Select(part => part.Name)
                        .ToList();

                    if (blocked.Count > 0)
                    {
                        opened.Dispose();
                        action_unlock();
                        MessageBox.Show(Properties.Resources.payload_incremental_warning
                            + "\n\n" + string.Join(", ", blocked));
                        return;
                    }

                    flashPayload(opened);
                }));
        }

        /// <summary>
        /// Returns a space separated list of payload partitions the device does not have,
        /// or null when every partition can be written.
        /// </summary>
        static string findUnknownPartitions(PayloadFile payload)
        {
            if (MainWindow.THIS.ignore_unknown_part.IsChecked == true)
                return null;

            List<string> missing = new List<string>();

            foreach (PayloadPartitionInfo part in payload.Partitions)
            {
                long size;
                if (fastbootData.partition_size.TryGetValue(part.Name, out size))
                    continue;

                if (fastbootData.current_slot != null &&
                    fastbootData.partition_size.TryGetValue(
                        part.Name + "_" + fastbootData.current_slot, out size))
                    continue;

                missing.Add(part.Name);
            }

            return missing.Count == 0 ? null : string.Join(" ", missing);
        }

        /// <summary>
        /// Extracts the whole payload first, in parallel, and only then writes it to the device.
        /// Nothing reaches the device until every image is present and its hash checks out, so a
        /// payload that turns out to be damaged cannot leave the phone half written. Each image
        /// is deleted as soon as it is flashed to keep the staging directory small.
        /// </summary>
        static void flashPayload(PayloadFile payload)
        {
            string staging = Path.Combine(PAYLOAD_TMP, "stage-" + Guid.NewGuid().ToString("N"));

            List<PayloadPartitionInfo> parts = payload.Partitions.ToList();
            long totalBytes = parts.Sum(part => part.UnpackedSize);
            if (totalBytes <= 0)
                totalBytes = 1;

            ExtractionOptions options = new ExtractionOptions
            {
                VerifyOperationHashes = true,
                VerifyImageHash = true,
                IgnoreUnsupportedOperations = false,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            };

            // Extraction counts for the first half of the bar, flashing for the second.
            long extractedBytes = 0;
            Dictionary<string, long> perPartition = new Dictionary<string, long>(StringComparer.Ordinal);
            int lastPercent = -1;

            Action<int> showPercent = new Action<int>(delegate (int percent)
            {
                if (percent == lastPercent)
                    return;
                lastPercent = percent;

                MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                {
                    MainWindow.THIS.fastboot_progress_bar.Value = percent;
                    Helper.TaskbarItemHelper.update(percent);
                }));
            });

            IProgress<ExtractionProgress> progress = new ExtractionProgressSink(delegate (ExtractionProgress value)
            {
                long done;
                lock (perPartition)
                {
                    perPartition[value.Partition] = value.BytesWritten;
                    done = 0;
                    foreach (long written in perPartition.Values)
                        done += written;
                    extractedBytes = done;
                }
                showPercent((int)(done * 50 / totalBytes));
            });

            action_lock();

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
                        CancellationToken.None);

                    if (!report.AllSucceeded)
                    {
                        error = string.Join("\n", report.Results
                            .Where(result => !result.Succeeded)
                            .Select(result => result.Partition + ": " + result.Error));
                    }
                    else
                    {
                        appendLog("Extracted " + Helper.byte2AUnit(report.TotalBytes)
                            + " in " + report.Elapsed.TotalSeconds.ToString("F1") + " s");

                        long flashedBytes = 0;

                        foreach (PartitionResult result in report.Results)
                        {
                            appendLog("Flashing " + result.Partition);

                            int? exitCode;
                            using (Fastboot fastboot = new Fastboot(cur_serial,
                                "flash \"" + result.Partition + "\" \"" + result.OutputPath + "\""))
                            {
                                while (true)
                                {
                                    string line = fastboot.stderr.ReadLine();
                                    if (line == null)
                                        break;
                                    appendLog(line);
                                }

                                exitCode = fastboot.WaitForExit(FlashTimeout);
                            }

                            // Without this the dialog reported success even when every write failed.
                            if (exitCode != 0)
                            {
                                error = result.Partition + ": fastboot " + (exitCode == null
                                    ? "did not finish within " + FlashTimeout.TotalMinutes + " minutes"
                                    : "exited with code " + exitCode);
                                appendLog(error);
                                break;
                            }

                            flashed++;
                            flashedBytes += result.Size;
                            showPercent(50 + (int)(flashedBytes * 50 / totalBytes));

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
                    load_fastboot_vars();

                    if (summary == null)
                    {
                        MessageBox.Show(Properties.Resources.operation_completed);
                        return;
                    }

                    Helper.TaskbarItemHelper.error();
                    MessageBox.Show(Properties.Resources.payload_error_occur
                        + "\n\n" + summary
                        + "\n\n" + flashedCount + " of " + parts.Count + " partitions were written");
                }));
            }));

            worker.IsBackground = true;
            worker.Start();
        }

        sealed class ExtractionProgressSink : IProgress<ExtractionProgress>
        {
            readonly Action<ExtractionProgress> sink;

            internal ExtractionProgressSink(Action<ExtractionProgress> sink)
            {
                this.sink = sink;
            }

            public void Report(ExtractionProgress value)
            {
                sink(value);
            }
        }

        public static void init()
        {
            devices = new List<fastboot_devices_row>();
            cur_status = FastbootStatus.show_devices;
            change_page();

            new Thread(new ThreadStart(devicesListRefresher)).Start();
            MainWindow.THIS.fastboot_devices_list.MouseDoubleClick += delegate
            {
                if (MainWindow.THIS.fastboot_devices_list.SelectedItems.Count == 0)
                    return;

                if (MainWindow.THIS.fastboot_devices_list.SelectedItems.Count > 1)
                {
                    MainWindow.THIS.fastboot_devices_list.SelectedItems.Clear();
                    return;
                }

                fastboot_devices_row cur = (fastboot_devices_row)MainWindow.THIS.fastboot_devices_list.SelectedItem;
                cur_serial = cur.serial;
                if (!checkCurDevExist())
                    return;
                cur_status = FastbootStatus.show_actions;
                MainWindow.THIS.fastboot_cur_device.Content = Properties.Resources.fastboot_current_device + cur.serial;
                change_page();
            };

            MainWindow.THIS.fastboot_remove.Click += delegate
            {
                cur_serial = null;
                cur_status = FastbootStatus.show_devices;
                change_page();
            };

            MainWindow.THIS.fastboot_reboot_d.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (fastbootData.fastbootd)
                {
                    new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("reboot bootloader", 2, false));
                }
                else
                {
                    new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("reboot fastboot", 3, false));
                }
            };

            MainWindow.THIS.fastboot_reboot_system.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("reboot", 0, false, true));

                cur_serial = null;
                cur_status = FastbootStatus.show_devices;
                change_page();
            };

            MainWindow.THIS.fastboot_reboot_recovery.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("reboot recovery", 0, false, true));

                cur_serial = null;
                cur_status = FastbootStatus.show_devices;
                change_page();
            };

            MainWindow.THIS.fastboot_ab_switch.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (fastbootData.current_slot == "a")
                {
                    new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("set_active b", 2, false));
                }
                else if (fastbootData.current_slot == "b")
                {
                    new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("set_active a", 2, false));
                }
                else
                {
                    MessageBox.Show(Properties.Resources.operation_not_supported);
                }
            };

            // Cancel a staged Virtual A/B update.
            MainWindow.THIS.fastboot_cancel_update.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("snapshot-update cancel", 2, true));
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

                Helper.fileSelect(new Helper.PathSelectCallback(delegate (string path)
                {
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
                    new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("flash " + ext_arg + " \"" + target + "\" \"" + path + "\"", -1, true));
                }), "Image File|*.img;*.image");
            };

            MainWindow.THIS.fastboot_erase.Click += delegate
            {
                if (singlePartitionCheck())
                    return;

                string target = ((fastboot_partition_row)MainWindow.THIS.fastboot_partition_list.SelectedItem).name;

                new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("erase \"" + target + "\"", 2, false));
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

                new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                .Start(new StepCmdRunnerParam("delete-logical-partition \"" + target + "\"", 2, false));
            };

            MainWindow.THIS.fastboot_logical_create.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                new FastbootActionWindow(FastbootActionWindow.StartType.CREATE, "", 0,
                    delegate (string name, ulong size)
                   {
                       new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                        .Start(new StepCmdRunnerParam(
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
                        new Thread(new ParameterizedThreadStart(step_cmd_runner_err))
                         .Start(new StepCmdRunnerParam(
                             "resize-logical-partition \"" + name + "\" \"" + size.ToString() + "\"", 2, false));
                    }).ShowDialog();
            };

            MainWindow.THIS.fastboot_flash_payload.Click += delegate
            {
                if (!checkCurDevExist())
                    return;

                if (vabStagingCheck())
                    return;

                Helper.fileSelect(new Helper.PathSelectCallback(delegate (string path)
                {
                    openPayloadThenFlash(path);
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

            MainWindow.THIS.fastboot_show_logs.Click += delegate
            {
                if ((bool)MainWindow.THIS.fastboot_show_logs.IsChecked)
                {
                    logger = new Logger(new Action(delegate
                    {
                        logger = null;
                        MainWindow.THIS.fastboot_show_logs.IsChecked = false;
                    }));
                    logger.Show();
                }
                else
                {
                    logger.Close();
                }
            };
        }
    }
}
