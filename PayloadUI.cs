using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ChromeosUpdateEngine;
using FastbootEnhance.Core.Payload;

namespace FastbootEnhance
{
    class PayloadUI
    {
        /// <summary>Only used when a package stores payload.bin deflated and it must be unpacked.</summary>
        public static readonly string PAYLOAD_TMP =
            Path.Combine(Path.GetTempPath(), "FastbootStudio", "dumper");

        enum page_status
        {
            empty,
            loaded
        }

        static page_status cur_status;
        public static PayloadFile payload;
        static CancellationTokenSource cancellation;
        static Thread extractionWorker;

        /// <summary>True while images are being written to the output folder.</summary>
        public static bool extracting => extractionWorker != null;

        static void switchMainView()
        {
            switch (cur_status)
            {
                case page_status.empty:
                    MainWindow.THIS.payload_before_load.Visibility = Visibility.Visible;
                    MainWindow.THIS.payload_after_load.Visibility = Visibility.Collapsed;
                    // The file chip and Remove live in the page header; with no file they mean nothing.
                    MainWindow.THIS.payload_action_bar.Visibility = Visibility.Collapsed;
                    break;
                case page_status.loaded:
                    MainWindow.THIS.payload_before_load.Visibility = Visibility.Collapsed;
                    MainWindow.THIS.payload_after_load.Visibility = Visibility.Visible;
                    MainWindow.THIS.payload_action_bar.Visibility = Visibility.Visible;
                    break;
            }
        }

        static void onLoad(string filename)
        {
            PayloadFile opened = null;
            Exception exception = null;

            MainWindow.THIS.payload_load_btn.Visibility = Visibility.Hidden;
            MainWindow.THIS.payload_opening.Visibility = Visibility.Visible;

            Action load = new Action(delegate
            {
                try
                {
                    opened = PayloadFile.Open(filename, PAYLOAD_TMP);
                }
                catch (Exception e)
                {
                    exception = e;
                }
            });

            Action afterLoad = new Action(delegate
            {
                MainWindow.THIS.payload_load_btn.Visibility = Visibility.Visible;
                MainWindow.THIS.payload_opening.Visibility = Visibility.Hidden;

                if (exception != null)
                {
                    LogStore.Append("Could not open " + filename + ": " + exception.Message);
                    ThemedDialog.Show(Properties.Resources.payload_unsupported_format
                        + "\n" + exception.Message);
                    return;
                }

                LogStore.Append("Opened " + filename + ": " + opened.Partitions.Count + " partitions, "
                    + (opened.IsIncremental ? "incremental" : "full") + " package");

                closeCurrent();
                payload = opened;
                cur_status = page_status.loaded;
                MainWindow.THIS.payload_cur_open.Content = Path.GetFileName(filename);
                MainWindow.THIS.payload_cur_open.ToolTip = Properties.Resources.payload_current_file + filename;
                refreshData();
                switchMainView();
            });

            Helper.offloadAndRun(load, afterLoad);
        }

        /// <summary>Opens a payload handed to the app from outside, e.g. on the command line.</summary>
        public static void openFromPath(string filename)
        {
            MainWindow.THIS.main_tabs.SelectedItem = MainWindow.THIS.payload_tab;
            onLoad(filename);
        }

        public static void closeCurrent()
        {
            if (payload == null)
                return;

            payload.Dispose();
            payload = null;
        }

        /// <summary>
        /// Stops a running extraction and gives its workers a moment to delete the images they
        /// had not finished, so closing the window does not leave truncated files behind.
        /// </summary>
        public static void cancelRunningWork()
        {
            CancellationTokenSource source = cancellation;
            if (source != null)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            Thread worker = extractionWorker;
            if (worker != null)
                worker.Join(TimeSpan.FromSeconds(10));
        }

        static void actionInit()
        {
            listHelper = new Helper.ListHelper<payload_partition_info_row>(MainWindow.THIS.payload_partition_info,
                new Helper.ListHelper<payload_partition_info_row>.Filter(
                    delegate (payload_partition_info_row row)
                    {
                        if (MainWindow.THIS.payload_partition_name_textbox.Text == "")
                            return true;

                        if (row.name.Contains(MainWindow.THIS.payload_partition_name_textbox.Text))
                            return true;
                        return false;
                    }
            ));

            MainWindow.THIS.payload_partition_name_textbox.TextChanged += delegate
            {
                listHelper.doFilter();
            };

            MainWindow.THIS.payload_load_btn.Click += delegate
            {
                Helper.fileSelect(new Helper.PathSelectCallback(delegate (string ret)
                {
                    onLoad(ret);
                }), "Payload or OTA package|*.bin;*.zip|All Files|*.*");
            };

            MainWindow.THIS.payload_remove.Click += delegate
            {
                closeCurrent();
                cur_status = page_status.empty;
                switchMainView();
            };

            MainWindow.THIS.payload_extract.Click += onExtractClicked;

            MainWindow.THIS.payload_before_load.DragEnter += delegate (object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effects = DragDropEffects.All;
                else
                    e.Effects = DragDropEffects.None;
            };

            MainWindow.THIS.payload_before_load.Drop += delegate (object sender, DragEventArgs e)
            {
                Array files = (Array)e.Data.GetData(DataFormats.FileDrop);
                if (files == null || files.Length == 0)
                    return;

                if (files.Length > 1)
                {
                    ThemedDialog.Show(Properties.Resources.payload_unable_drop_multifile);
                    return;
                }

                onLoad(files.GetValue(0).ToString());
            };
        }

        static void onExtractClicked(object sender, RoutedEventArgs args)
        {
            if (payload == null)
                return;

            if (MainWindow.THIS.payload_partition_info.SelectedItems.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.payload_target_partition_not_selected);
                return;
            }

            List<string> selected = MainWindow.THIS.payload_partition_info.SelectedItems
                .Cast<payload_partition_info_row>()
                .Select(row => row.name)
                .ToList();

            bool allowIncremental = MainWindow.THIS.payload_ignore_delta.IsChecked == true;
            bool ignoreUnsupported = MainWindow.THIS.payload_ignore_unknown_op.IsChecked == true;
            bool skipChecks = MainWindow.THIS.payload_ignore_check.IsChecked == true;

            // Only block when something actually selected cannot be rebuilt from this package.
            List<string> blocked = selected
                .Select(name => payload.FindPartition(name))
                .Where(info => info != null && !info.CanExtract)
                .Select(info => info.Name)
                .ToList();

            if (blocked.Count > 0 && !allowIncremental && !ignoreUnsupported)
            {
                ThemedDialog.Show(Properties.Resources.payload_incremental_warning
                    + "\n\n" + string.Join(", ", blocked));
                return;
            }

            // "Allow incremental" only lets the extraction start: partitions that can be rebuilt
            // from this package come out whole, the rest fail with the reason. Leaving blocks as
            // zeros is a separate, explicit choice ("ignore unknown operations").
            Helper.pathSelect(new Helper.PathSelectCallback(delegate (string path)
            {
                startExtraction(selected, path, ignoreUnsupported, skipChecks);
            }));
        }

        static void startExtraction(
            List<string> selected, string outputDirectory, bool ignoreUnsupported, bool skipChecks)
        {
            MainWindow.THIS.payload_progress.Visibility = Visibility.Visible;
            MainWindow.THIS.payload_progress.Value = 0;
            MainWindow.THIS.payload_action_bar.Visibility = Visibility.Hidden;
            MainWindow.THIS.payload_extract.IsEnabled = false;
            MainWindow.THIS.payload_extract_options.IsEnabled = false;
            Helper.TaskbarItemHelper.start();

            ExtractionOptions options = new ExtractionOptions
            {
                VerifyOperationHashes = !skipChecks,
                VerifyImageHash = !skipChecks,
                IgnoreUnsupportedOperations = ignoreUnsupported,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            };

            long totalBytes = selected
                .Select(name => payload.FindPartition(name))
                .Where(info => info != null)
                .Sum(info => info.UnpackedSize);

            ProgressAggregator aggregator = new ProgressAggregator(totalBytes, delegate (int percent)
            {
                MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                {
                    MainWindow.THIS.payload_progress.Value = percent;
                    Helper.TaskbarItemHelper.update(percent);
                }));
            });

            CancellationTokenSource source = new CancellationTokenSource();
            cancellation = source;
            PayloadFile target = payload;

            Thread worker = new Thread(new ThreadStart(delegate
            {
                ExtractionReport report = null;
                Exception failure = null;

                try
                {
                    report = PayloadExtractor.ExtractAll(
                        target, outputDirectory, selected, options, aggregator, source.Token);
                }
                catch (OperationCanceledException)
                {
                    failure = null;
                }
                catch (Exception e)
                {
                    failure = e;
                }

                MainWindow.THIS.Dispatcher.BeginInvoke(new Action(delegate
                {
                    MainWindow.THIS.payload_progress.Visibility = Visibility.Hidden;
                    MainWindow.THIS.payload_action_bar.Visibility = Visibility.Visible;
                    MainWindow.THIS.payload_extract.IsEnabled = true;
                    MainWindow.THIS.payload_extract_options.IsEnabled = true;
                    Helper.TaskbarItemHelper.stop();

                    cancellation = null;
                    extractionWorker = null;
                    source.Dispose();

                    if (failure != null)
                    {
                        ThemedDialog.Show(Properties.Resources.payload_error_occur + "\n" + failure.Message);
                        return;
                    }

                    if (report == null)
                        return;

                    LogStore.Append("Extracted " + report.SucceededCount + " of " + report.Results.Count
                        + " partitions to " + outputDirectory + " in " + report.Elapsed.TotalSeconds.ToString("F1") + " s");

                    // Images with skipped operations are partly zero and were never checked; say so.
                    string incomplete = string.Join("\n", report.Results
                        .Where(r => r.Succeeded && r.SkippedOperations > 0)
                        .Select(r => r.Partition + ": " + string.Format(Properties.Resources.extract_incomplete_detail, r.SkippedOperations)));

                    if (report.AllSucceeded && incomplete.Length == 0)
                    {
                        ThemedDialog.Done(Properties.Resources.operation_completed + "\n\n"
                            + string.Format(Properties.Resources.extract_done_detail,
                                Helper.byte2AUnit(report.TotalBytes), report.Elapsed.TotalSeconds.ToString("F1")));
                        return;
                    }

                    string details = string.Join("\n", report.Results
                        .Where(r => !r.Succeeded)
                        .Select(r => r.Partition + ": " + r.Error));

                    string message = details.Length > 0 ? Properties.Resources.payload_error_occur + "\n\n" + details : "";
                    if (incomplete.Length > 0)
                        message += (message.Length > 0 ? "\n\n" : "") + Properties.Resources.extract_incomplete + "\n\n" + incomplete;

                    ThemedDialog.Show(message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            }));

            worker.IsBackground = true;
            extractionWorker = worker;
            worker.Start();
        }

        static void refreshData()
        {
            MainWindow.THIS.payload_progress.Visibility = Visibility.Hidden;
            MainWindow.THIS.payload_action_bar.Visibility = Visibility.Visible;
            MainWindow.THIS.payload_info.Items.Clear();
            listHelper.clear();
            MainWindow.THIS.payload_partition_name_textbox.Text = "";
            MainWindow.THIS.payload_dynamic_partition_meta.Items.Clear();

            DeltaArchiveManifest manifest = payload.Manifest;

            payloadInfoListAppend(Properties.Resources.payload_version, payload.FileFormatVersion.ToString());
            payloadInfoListAppend(Properties.Resources.payload_data_size, Helper.byte2AUnit(payload.DataLength));

            payloadInfoListAppend(Properties.Resources.payload_metadata_signature,
                describeSignature(payload.MetadataSignature));
            payloadInfoListAppend(Properties.Resources.payload_signature,
                describeSignature(payload.PayloadSignature));

            payloadInfoListAppend(Properties.Resources.payload_full_package,
                payload.IsIncremental
                    ? Properties.Resources.no + " (minor version " + payload.MinorVersion + ")"
                    : Properties.Resources.yes);

            if (manifest.HasMaxTimestamp)
            {
                DateTime? stamp = Helper.timeStamp2DataTime(manifest.MaxTimestamp);
                payloadInfoListAppend(Properties.Resources.payload_timestamp,
                    stamp != null ? stamp.Value.ToString() : manifest.MaxTimestamp.ToString());
            }

            payloadInfoListAppend(Properties.Resources.payload_blocksize, Helper.byte2AUnit(payload.BlockSize));

            payloadInfoListAppend(Properties.Resources.payload_container, payload.Source.FromZip
                ? (payload.Source.ReadInPlace ? Properties.Resources.payload_container_inplace : Properties.Resources.payload_container_unpacked)
                : "payload.bin");

            appendOperationBreakdown();

            // The ChromeOS-era image info fields are reserved in Android's manifest; these are
            // what Android payloads actually carry.
            if (manifest.HasSecurityPatchLevel)
                payloadInfoListAppend(Properties.Resources.payload_security_patch, manifest.SecurityPatchLevel);

            if (manifest.HasPartialUpdate)
                payloadInfoListAppend(Properties.Resources.payload_partial_update,
                    manifest.PartialUpdate ? Properties.Resources.yes : Properties.Resources.no);

            if (manifest.ApexInfo.Count > 0)
                payloadInfoListAppend(Properties.Resources.payload_apex, manifest.ApexInfo.Count.ToString());

            foreach (PayloadPartitionInfo part in payload.Partitions)
            {
                payloadPartitionInfoListAppend(
                    part.Name,
                    Helper.byte2AUnit(part.UnpackedSize),
                    part.ExpectedSha256 ?? Properties.Resources.unknown,
                    string.Join(", ", part.Codecs),
                    describeSupport(part));
            }
            listHelper.render();

            if (manifest.DynamicPartitionMetadata != null)
            {
                if (manifest.DynamicPartitionMetadata.HasSnapshotEnabled)
                    dynamicPartitionMetaAppend(Properties.Resources.payload_snapshot_enabled +
                        (manifest.DynamicPartitionMetadata.SnapshotEnabled ? Properties.Resources.yes :
                        Properties.Resources.no));

                foreach (DynamicPartitionGroup group in manifest.DynamicPartitionMetadata.Groups)
                {
                    dynamicPartitionMetaAppend(Properties.Resources.payload_partition_group_name + group.Name);
                    dynamicPartitionMetaAppend(Properties.Resources.payload_partition_group_size
                        + Helper.byte2AUnit(group.Size));
                    dynamicPartitionMetaAppend(Properties.Resources.payload_partition_group_include);
                    foreach (string partition in group.PartitionNames)
                        dynamicPartitionMetaAppend(partition);
                }
            }
            else
            {
                dynamicPartitionMetaAppend(Properties.Resources.payload_no_dynamic_metadata);
            }
        }

        static void appendOperationBreakdown()
        {
            IReadOnlyList<KeyValuePair<string, int>> counts = payload.OperationCounts();
            int total = counts.Sum(pair => pair.Value);

            payloadInfoListAppend(Properties.Resources.payload_operations, total.ToString());

            foreach (KeyValuePair<string, int> pair in counts)
            {
                double share = total > 0 ? 100.0 * pair.Value / total : 0;
                payloadInfoListAppend("  " + pair.Key, pair.Value + " (" + share.ToString("F1") + "%)");
            }
        }

        /// <summary>
        /// Describes a signature block without indexing into it blindly: a payload may carry
        /// no signatures at all, which used to throw before anything was shown.
        /// </summary>
        static string describeSignature(ChromeosUpdateEngine.Signatures signatures)
        {
            if (signatures == null || signatures.Signatures_.Count == 0)
                return Properties.Resources.no;

            ChromeosUpdateEngine.Signatures.Types.Signature first = signatures.Signatures_[0];
            string summary = signatures.Signatures_.Count + " x ";
            return summary + (first.HasData ? first.Data.ToBase64() : Properties.Resources.unknown);
        }

        static string describeSupport(PayloadPartitionInfo part)
        {
            switch (part.Support)
            {
                case OperationSupportKind.Supported:
                    return Properties.Resources.yes;
                case OperationSupportKind.RequiresSource:
                    return Properties.Resources.no + " (needs source image)";
                default:
                    return Properties.Resources.no;
            }
        }

        class payload_info_row
        {
            public string title { get; }
            public string value { get; }
            public payload_info_row(string title, string value)
            {
                this.title = title;
                this.value = Helper.ltr(value);
            }
        }

        static Helper.ListHelper<payload_partition_info_row> listHelper;

        static void payloadInfoListAppend(string title, string value)
        {
            MainWindow.THIS.payload_info.Items.Add(new payload_info_row(title, value));
        }

        class payload_partition_info_row
        {
            public string name { get; }
            public string size { get; }
            public string hash { get; }
            public string codecs { get; }
            public string extractable { get; }

            public payload_partition_info_row(
                string name, string size, string hash, string codecs, string extractable)
            {
                this.name = name;
                this.size = size;
                this.hash = hash;
                this.codecs = codecs;
                this.extractable = extractable;
            }
        }

        static void dynamicPartitionMetaAppend(string line)
        {
            MainWindow.THIS.payload_dynamic_partition_meta.Items.Add(new payload_dynamic_partition_meta_row(line));
        }

        class payload_dynamic_partition_meta_row
        {
            public string line { get; }
            public payload_dynamic_partition_meta_row(string line)
            {
                this.line = line;
            }
        }

        static void payloadPartitionInfoListAppend(
            string name, string size, string hash, string codecs, string extractable)
        {
            listHelper.addItem(new payload_partition_info_row(name, size, hash, codecs, extractable));
        }

        public static void init()
        {
            cur_status = page_status.empty;
            actionInit();
            switchMainView();
        }
    }
}
