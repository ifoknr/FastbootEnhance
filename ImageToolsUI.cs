using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using FastbootEnhance.Core;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance
{
    /// <summary>
    /// The Image Tools page: inspects firmware images, converts between sparse and raw
    /// (simg2img, img2simg, simg2simg) and unpacks super images (lpunpack), all in-process.
    /// Work runs on one worker thread; the page is updated through the dispatcher.
    /// </summary>
    static class ImageToolsUI
    {
        static MainWindow W => MainWindow.THIS;

        static IList<string> parts;

        /// <summary>Set when the open image is pieces of a Qualcomm flash package (super_1.img ...).</summary>
        static RawProgramImage pieces;
        static ImageInfo info;
        static SuperImage super;
        static string outputFolder;
        static string lastOutput;

        static readonly ObservableCollection<SuperRow> superRows = new ObservableCollection<SuperRow>();

        public static volatile bool busy;
        static CancellationTokenSource cancel;
        static volatile Thread worker;

        const int MaxChunkRows = 5000;

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
            W.images_partitions.ItemsSource = superRows;
            showLoaded(false);

            W.images_open.Click += delegate { pickFiles(); };
            W.images_open_other.Click += delegate { pickFiles(); };
            W.images_close.Click += delegate
            {
                if (!busy)
                    close();
            };

            foreach (UIElement target in new UIElement[] { W.images_empty, W.images_loaded })
            {
                target.DragEnter += delegate (object sender, DragEventArgs e)
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !busy ? DragDropEffects.Copy : DragDropEffects.None;
                    e.Handled = true;
                };
                target.DragOver += delegate (object sender, DragEventArgs e)
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !busy ? DragDropEffects.Copy : DragDropEffects.None;
                    e.Handled = true;
                };
                target.Drop += delegate (object sender, DragEventArgs e)
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0 && !busy)
                        open(files);
                };
            }

            W.images_select_all.Click += delegate
            {
                foreach (SuperRow row in superRows)
                    row.Selected = true;
            };
            W.images_select_none.Click += delegate
            {
                foreach (SuperRow row in superRows)
                    row.Selected = false;
            };

            W.images_change_folder.Click += delegate
            {
                Helper.pathSelect(delegate (string path)
                {
                    outputFolder = path;
                    showOutputFolder();
                });
            };
            W.images_open_folder.Click += delegate { openInExplorer(lastOutput ?? outputFolder); };

            W.images_split.Checked += delegate { W.images_split_mb.IsEnabled = true; };
            W.images_split.Unchecked += delegate { W.images_split_mb.IsEnabled = !(info != null && !info.IsSparse); };

            W.images_to_raw.Click += delegate { toRaw(); };
            W.images_to_sparse.Click += delegate { toSparse(); };
            W.images_extract.Click += delegate { extract(); };
            W.images_cancel.Click += delegate
            {
                CancellationTokenSource source = cancel;
                if (source != null)
                    source.Cancel();
            };
        }

        /// <summary>Stops a running conversion when the window closes and removes its partial output.</summary>
        public static void shutdown()
        {
            CancellationTokenSource source = cancel;
            if (source != null)
                source.Cancel();
            Thread running = worker;
            if (running != null)
                running.Join(TimeSpan.FromSeconds(10));
        }

        // ------------------------------------------------------------------ open

        static void pickFiles()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "Android images|*.img;*.simg;*.raw;*.bin;*sparsechunk*;*.img.*|All files|*.*",
            };
            if (dialog.ShowDialog() == true)
                open(dialog.FileNames);
        }

        /// <summary>Opens an image passed on the command line or dropped on the program.</summary>
        public static void openFromPath(string path)
        {
            if (!busy)
                open(new[] { path });
        }

        static void open(string[] files)
        {
            W.images_opening.Visibility = Visibility.Visible;
            W.images_open.Visibility = Visibility.Hidden;

            Helper.offloadAndRun(delegate
            {
                IList<string> chosen;
                ImageInfo opened = null;
                SuperImage table = null;
                List<SparseImage> sparse = new List<SparseImage>();
                RawProgramImage pieced = null;
                Exception failure = null;
                try
                {
                    // Pieces of a Qualcomm flash package, placed by its rawprogram XML.
                    pieced = RawProgramImage.TryFind(files[0]);
                    if (pieced != null)
                    {
                        chosen = pieced.Files;
                        using (Stream stream = pieced.Open())
                        {
                            opened = ImageProbe.Identify(stream);
                            if (opened.Kind == ImageKind.Super)
                                table = SuperImage.Read(stream);
                        }
                    }
                    else
                    {
                        // Several files are parts of one image, in the order of their numbers.
                        chosen = files.Length == 1
                            ? SparseConverter.FindParts(files[0])
                            : ImageNaming.OrderParts(files);

                        opened = ImageProbe.Identify(chosen[0]);
                        if (opened.IsSparse)
                            sparse.AddRange(chosen.Select(SparseImage.Open));
                        else if (chosen.Count > 1)
                            throw new InvalidDataException(Properties.Resources.images_parts_not_sparse);

                        if (opened.Kind == ImageKind.Super)
                        {
                            using (Stream stream = openImage(chosen, null))
                                table = SuperImage.Read(stream);
                        }
                    }
                    parts = chosen;
                    pieces = pieced;
                }
                catch (Exception e)
                {
                    failure = e;
                    chosen = null;
                }

                W.Dispatcher.Invoke(delegate
                {
                    W.images_opening.Visibility = Visibility.Hidden;
                    W.images_open.Visibility = Visibility.Visible;
                    if (failure != null)
                    {
                        log("image tools: cannot open " + files[0] + ": " + failure.Message);
                        ThemedDialog.Show(failure.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    show(chosen, opened, sparse, table);
                });
            }, delegate { });
        }

        static Stream openImage(IList<string> files, RawProgramImage pieced)
        {
            if (pieced != null)
                return pieced.Open();
            if (SparseImage.IsSparse(files[0]))
                return SparseStream.Open(files);
            return new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
        }

        static string kindName(ImageKind kind)
        {
            switch (kind)
            {
                case ImageKind.Super: return Properties.Resources.images_kind_super;
                case ImageKind.Ext4: return Properties.Resources.images_kind_ext4;
                case ImageKind.Erofs: return Properties.Resources.images_kind_erofs;
                case ImageKind.F2fs: return Properties.Resources.images_kind_f2fs;
                case ImageKind.BootImage: return Properties.Resources.images_kind_boot;
                case ImageKind.VendorBootImage: return Properties.Resources.images_kind_vendor_boot;
                case ImageKind.Vbmeta: return Properties.Resources.images_kind_vbmeta;
                case ImageKind.Dtbo: return "DTBO";
                case ImageKind.Payload: return Properties.Resources.images_kind_payload;
                case ImageKind.Zip: return Properties.Resources.images_kind_zip;
                default: return Properties.Resources.unknown;
            }
        }

        static void show(IList<string> chosen, ImageInfo opened, List<SparseImage> sparse, SuperImage table)
        {
            info = opened;
            super = table;
            outputFolder = Path.GetDirectoryName(Path.GetFullPath(chosen[0]));
            lastOutput = null;
            log("image tools: opened " + string.Join(", ", chosen.Select(Path.GetFileName)) + " (" + opened.Kind + (opened.IsSparse ? ", sparse" : "") + ")");

            W.images_file_name.Text = Path.GetFileName(chosen[0]) + (chosen.Count > 1 ? "  +" + (chosen.Count - 1) : "");
            W.images_file_name.ToolTip = string.Join("\n", chosen);

            // Details
            List<InfoRow> rows = new List<InfoRow>
            {
                new InfoRow(Properties.Resources.images_row_file, Path.GetFileName(chosen[0])
                    + (chosen.Count > 1 ? "  (" + string.Format(Properties.Resources.images_parts, chosen.Count) + ")" : "")),
                new InfoRow(Properties.Resources.images_row_format, pieces != null
                    ? string.Format(Properties.Resources.images_format_pieces, Helper.ltr(Path.GetFileName(pieces.Xml)))
                    : opened.IsSparse
                    ? "Android sparse " + sparse[0].MajorVersion + "." + sparse[0].MinorVersion
                    : Properties.Resources.images_format_raw),
                new InfoRow(Properties.Resources.images_row_content, kindName(opened.Kind)),
                new InfoRow(Properties.Resources.images_row_size, Helper.byte2AUnit(opened.ImageLength)),
                new InfoRow(Properties.Resources.images_row_disk, Helper.byte2AUnit(chosen.Sum(f => new FileInfo(f).Length))),
            };
            if (opened.IsSparse)
            {
                int blockSize = sparse[0].BlockSize;
                long raw = sparse.Sum(s => s.CountBlocks(SparseChunkType.Raw));
                long fill = sparse.Sum(s => s.CountBlocks(SparseChunkType.Fill));
                long total = sparse[0].TotalBlocks;
                rows.Add(new InfoRow(Properties.Resources.images_row_block_size, Helper.byte2AUnit(blockSize)));
                rows.Add(new InfoRow(Properties.Resources.images_row_blocks, total.ToString()));
                rows.Add(new InfoRow(Properties.Resources.images_row_block_kinds, string.Format(Properties.Resources.images_blocks_detail,
                    total, raw, fill, Math.Max(0, total - raw - fill))));
                rows.Add(new InfoRow(Properties.Resources.images_row_chunks, sparse.Sum(s => s.Chunks.Count).ToString()));
                uint checksum = sparse.Count == 1 ? sparse[0].ImageChecksum : 0;
                rows.Add(new InfoRow(Properties.Resources.images_row_crc, checksum == 0
                    ? Properties.Resources.images_crc_none : checksum.ToString("x8")));
            }
            if (table != null)
            {
                rows.Add(new InfoRow(Properties.Resources.images_row_metadata, string.Format(Properties.Resources.images_metadata_detail,
                    table.MajorVersion + "." + table.MinorVersion, table.MetadataSlotCount)
                    + (table.UsedBackup ? "  · " + Properties.Resources.images_backup_copy : "")));
                rows.Add(new InfoRow(Properties.Resources.images_row_vab, table.IsVirtualAB ? Properties.Resources.yes : Properties.Resources.no));
                rows.Add(new InfoRow(Properties.Resources.images_row_partitions, string.Format(Properties.Resources.images_partitions_detail,
                    table.Partitions.Count(p => !p.IsEmpty), table.Partitions.Count)));
                foreach (SuperGroup group in table.Groups.Where(g => g.Name != "default" || table.Groups.Count == 1))
                {
                    rows.Add(new InfoRow(Properties.Resources.images_row_group, group.Name + "  ·  "
                        + (group.MaximumSize == 0 ? Properties.Resources.images_unlimited : Helper.byte2AUnit((long)group.MaximumSize))));
                }
                if (table.BlockDevices.Count > 0)
                    rows.Add(new InfoRow(Properties.Resources.images_row_super_size, Helper.byte2AUnit((long)table.BlockDevices[0].Size)));
            }
            W.images_details.ItemsSource = rows;

            // Partitions of a super image
            superRows.Clear();
            if (table != null)
            {
                foreach (SuperPartition partition in table.Partitions)
                    superRows.Add(new SuperRow(partition));
            }

            // Chunks of a sparse image (the first few thousand; a full list adds nothing)
            List<ChunkRow> chunks = new List<ChunkRow>();
            int index = 0;
            foreach (SparseImage image in sparse)
            {
                foreach (SparseChunk chunk in image.Chunks)
                {
                    if (chunks.Count >= MaxChunkRows)
                        break;
                    chunks.Add(new ChunkRow(index++, chunk, image.BlockSize));
                }
            }
            W.images_chunks.ItemsSource = chunks;

            W.images_partitions_tab.Visibility = table != null ? Visibility.Visible : Visibility.Collapsed;
            W.images_chunks_tab.Visibility = opened.IsSparse ? Visibility.Visible : Visibility.Collapsed;
            W.images_tabs.SelectedItem = table != null ? W.images_partitions_tab : W.images_details_tab;

            // Actions that make sense for this image
            W.images_extract.Visibility = table != null ? Visibility.Visible : Visibility.Collapsed;
            W.images_to_raw.Visibility = opened.IsSparse || pieces != null ? Visibility.Visible : Visibility.Collapsed;
            W.images_to_raw.Content = pieces != null ? Properties.Resources.images_combine_raw : Properties.Resources.images_to_raw;
            W.images_to_raw.Style = (Style)W.FindResource(pieces != null ? typeof(System.Windows.Controls.Button) : (object)"AccentButton");
            W.images_to_sparse.Visibility = Visibility.Visible;
            W.images_to_sparse.Content = pieces != null ? Properties.Resources.images_combine_sparse
                : opened.IsSparse ? Properties.Resources.images_resplit : Properties.Resources.images_to_sparse;
            W.images_to_sparse.Style = (Style)W.FindResource(pieces != null || !(opened.IsSparse || table != null)
                ? (object)"AccentButton" : typeof(System.Windows.Controls.Button));
            // Re-splitting a sparse image always needs a size; for raw input splitting is optional.
            // Combined pieces are written as one file.
            W.images_split.Visibility = opened.IsSparse || pieces != null ? Visibility.Collapsed : Visibility.Visible;
            W.images_split_row.Visibility = pieces != null ? Visibility.Collapsed : Visibility.Visible;
            W.images_split_mb.IsEnabled = opened.IsSparse || W.images_split.IsChecked == true;
            W.images_note.Text = pieces != null
                ? string.Format(Properties.Resources.images_note_pieces, pieces.Pieces.Count, Helper.ltr(pieces.Label), Helper.ltr(Path.GetFileName(pieces.Xml)))
                : table != null ? Properties.Resources.images_note_super
                : opened.IsSparse ? Properties.Resources.images_note_sparse
                : Properties.Resources.images_note_raw;

            W.images_progress.Value = 0;
            W.images_progress_text.Text = "";
            showOutputFolder();
            showLoaded(true);
        }

        static void close()
        {
            parts = null;
            pieces = null;
            info = null;
            super = null;
            superRows.Clear();
            W.images_details.ItemsSource = null;
            W.images_chunks.ItemsSource = null;
            showLoaded(false);
        }

        static void showLoaded(bool loaded)
        {
            W.images_empty.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            W.images_loaded.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            W.images_header_actions.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        }

        static void showOutputFolder()
        {
            W.images_out_folder.Text = outputFolder;
            W.images_out_folder.ToolTip = outputFolder;
        }

        static void setBusy(bool value)
        {
            busy = value;
            foreach (UIElement control in new UIElement[]
            {
                W.images_extract, W.images_to_raw, W.images_to_sparse, W.images_split, W.images_split_mb,
                W.images_change_folder, W.images_open_other, W.images_close, W.images_select_all,
                W.images_select_none, W.images_partitions,
            })
            {
                control.IsEnabled = !value;
            }
            if (!value)
                W.images_split_mb.IsEnabled = info != null && (info.IsSparse || W.images_split.IsChecked == true);
            W.images_cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
                Helper.TaskbarItemHelper.start();
            else
                Helper.TaskbarItemHelper.stop();
        }

        // ------------------------------------------------------------------ work

        /// <summary>The image's name without extensions or part numbers: "super" for super.img_sparsechunk.3.</summary>
        static string stem()
        {
            if (pieces != null)
                return pieces.Label;
            string name = Path.GetFileName(parts[0]);
            name = Regex.Replace(name, @"([._]sparsechunk)?\.\d+$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"(\.(img|simg|raw|bin))+$", "", RegexOptions.IgnoreCase);
            return name.Length == 0 ? "image" : name;
        }

        static bool confirmOverwrite(IEnumerable<string> paths)
        {
            List<string> existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (existing.Count == 0)
                return true;
            return Helper.confirm(string.Format(Properties.Resources.images_overwrite,
                string.Join("\n", existing.Select(Path.GetFileName))), Properties.Resources.confirm_title, true);
        }

        /// <summary>Runs one job on a worker thread, with progress, cancellation and clean-up of partial output.</summary>
        static void run(string what, Func<CancellationToken, Action<long, long>, string> work, IList<string> cleanupOnFailure)
        {
            if (busy)
                return;
            CancellationTokenSource source = new CancellationTokenSource();
            cancel = source;
            setBusy(true);
            W.images_progress.Value = 0;
            W.images_progress_text.Text = what;
            Stopwatch clock = Stopwatch.StartNew();
            log("image tools: " + what);

            long lastShown = -1;
            Action<long, long> report = (done, total) =>
            {
                int percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                if (percent == lastShown)
                    return;
                lastShown = percent;
                ui(delegate
                {
                    W.images_progress.Value = percent;
                    W.images_progress_text.Text = what + "  ·  " + percent + "%";
                    Helper.TaskbarItemHelper.update(percent);
                });
            };

            Thread thread = new Thread(delegate ()
            {
                string message = null;
                Exception failure = null;
                try
                {
                    message = work(source.Token, report);
                }
                catch (Exception e)
                {
                    failure = e;
                    foreach (string path in cleanupOnFailure)
                        deleteQuietly(path);
                }

                string elapsed = clock.Elapsed.TotalSeconds.ToString("F1");
                ui(delegate
                {
                    worker = null;
                    cancel = null;
                    setBusy(false);
                    if (failure == null)
                    {
                        W.images_progress.Value = 100;
                        W.images_progress_text.Text = string.Format(Properties.Resources.images_done_in, elapsed);
                        log("image tools: done in " + elapsed + " s");
                        ThemedDialog.Done(message);
                    }
                    else if (failure is OperationCanceledException)
                    {
                        W.images_progress.Value = 0;
                        W.images_progress_text.Text = "";
                        log("image tools: cancelled");
                        ThemedDialog.Show(Properties.Resources.backup_cancelled, Properties.Resources.nav_images,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        W.images_progress_text.Text = "";
                        Helper.TaskbarItemHelper.error();
                        log("image tools: failed: " + failure.Message);
                        ThemedDialog.Show(failure.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
            thread.IsBackground = true;
            worker = thread;
            thread.Start();
        }

        sealed class Progress : IProgress<long>
        {
            readonly Action<long> action;
            public Progress(Action<long> action) { this.action = action; }
            public void Report(long value) { action(value); }
        }

        static void toRaw()
        {
            if (pieces != null)
            {
                combinePieces(false);
                return;
            }
            IList<string> inputs = parts;
            long total = info.ImageLength;
            string output = Path.Combine(outputFolder, stem() + ".raw.img");
            if (!confirmOverwrite(new[] { output }))
                return;

            run(Properties.Resources.images_to_raw, (token, report) =>
            {
                ExpandResult result = SparseConverter.ToRaw(inputs, output, new Progress(done => report(done, total)), token);
                lastOutput = outputFolder;
                return string.Format(Properties.Resources.images_raw_done, output, Helper.byte2AUnit(result.Bytes))
                    + "\n" + string.Format(Properties.Resources.images_crc_line, result.Crc32.ToString("x8"))
                    + (result.ChecksumsChecked > 0 ? "  ✓ " + string.Format(Properties.Resources.images_crc_checked, result.ChecksumsChecked) : "");
            }, new[] { output });
        }

        /// <summary>
        /// Puts the pieces of a Qualcomm package together: raw at its full size, or sparse with
        /// the gaps left out, which is what fastboot flashes.
        /// </summary>
        static void combinePieces(bool sparse)
        {
            RawProgramImage source = pieces;
            string output = Path.Combine(outputFolder, stem() + (sparse ? ".img" : ".raw.img"));
            if (source.Files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
                output = Path.Combine(outputFolder, stem() + (sparse ? ".combined.img" : ".combined.raw.img"));
            if (!confirmOverwrite(new[] { output }))
                return;
            long total = Math.Max(1, source.DataBytes);
            SuperImage table = super;

            run(sparse ? Properties.Resources.images_combine_sparse : Properties.Resources.images_combine_raw, (token, report) =>
            {
                Progress progress = new Progress(done => report(done, total));
                long bytes = sparse ? source.WriteSparse(output, progress, token) : source.WriteRaw(output, progress, token);

                // A combined super has to read back as super, with the same partitions.
                if (table != null)
                {
                    using (Stream check = sparse ? (Stream)SparseStream.Open(output) : File.OpenRead(output))
                    {
                        SuperImage read = SuperImage.Read(check);
                        if (!read.Partitions.Select(p => p.Name).SequenceEqual(table.Partitions.Select(p => p.Name)))
                            throw new InvalidDataException("the combined image does not read back as the same super");
                    }
                }
                lastOutput = outputFolder;
                log("  " + source.Pieces.Count + " pieces -> " + output);
                return string.Format(Properties.Resources.images_combine_done, source.Pieces.Count, output, Helper.byte2AUnit(bytes),
                    sparse ? Properties.Resources.images_combine_sparse_note : Properties.Resources.images_combine_raw_note);
            }, new[] { output });
        }

        static long? splitBytes(bool required)
        {
            if (!required && W.images_split.IsChecked != true)
                return null;
            long megabytes;
            if (!long.TryParse(W.images_split_mb.Text.Trim(), out megabytes) || megabytes < 1 || megabytes > 1 << 20)
            {
                ThemedDialog.Show(Properties.Resources.images_split_invalid, Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return -1;
            }
            return megabytes << 20;
        }

        static void toSparse()
        {
            if (pieces != null)
            {
                combinePieces(true);
                return;
            }
            bool resplit = info.IsSparse;
            long? split = splitBytes(resplit);
            if (split == -1)
                return;

            string input = parts[0];
            if (resplit && parts.Count > 1)
            {
                ThemedDialog.Show(Properties.Resources.images_resplit_one, Properties.Resources.nav_images,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string output = split.HasValue
                ? Path.Combine(outputFolder, stem() + ".img_sparsechunk")
                : Path.Combine(outputFolder, stem() + ".sparse.img");
            string firstPart = split.HasValue ? output + ".0" : output;
            if (!confirmOverwrite(new[] { firstPart }))
                return;
            long total = new FileInfo(input).Length;

            run(resplit ? Properties.Resources.images_resplit : Properties.Resources.images_to_sparse, (token, report) =>
            {
                SparseWriteResult result = SparseConverter.ToSparse(input, output, 4096, split,
                    new Progress(done => report(done, total)), token);
                lastOutput = outputFolder;
                return string.Format(Properties.Resources.images_sparse_done, result.Files.Count, Helper.byte2AUnit(result.Bytes),
                    string.Join("\n", result.Files.Select(Path.GetFileName).Take(12)) + (result.Files.Count > 12 ? "\n…" : ""));
            }, split.HasValue ? Enumerable.Range(0, 4096).Select(i => output + "." + i).ToList() : new List<string> { output });
        }

        static void extract()
        {
            List<SuperRow> chosen = superRows.Where(r => r.Selected && r.CanExtract).ToList();
            if (chosen.Count == 0)
            {
                ThemedDialog.Show(Properties.Resources.images_nothing_selected);
                return;
            }

            IList<string> inputs = parts;
            RawProgramImage pieced = pieces;
            string folder = Path.Combine(outputFolder, stem() + "_unpacked");
            List<string> outputs = chosen.Select(r => Path.Combine(folder, r.Name + ".img")).ToList();
            if (!confirmOverwrite(outputs))
                return;
            foreach (SuperRow row in superRows)
            {
                row.Progress = 0;
                row.Current = chosen.Contains(row) ? SuperRow.Stage.Queued : SuperRow.Stage.Idle;
            }
            long total = Math.Max(1, chosen.Sum(r => r.Partition.Size));

            run(Properties.Resources.images_extract, (token, report) =>
            {
                Directory.CreateDirectory(folder);
                long before = 0;
                List<string> failed = new List<string>();
                using (Stream stream = openImage(inputs, pieced))
                {
                    foreach (SuperRow row in chosen)
                    {
                        if (token.IsCancellationRequested)
                        {
                            ui(delegate { row.Current = SuperRow.Stage.Skipped; });
                            continue;
                        }
                        ui(delegate { row.Current = SuperRow.Stage.Extracting; });
                        string path = Path.Combine(folder, row.Name + ".img");
                        long size = row.Partition.Size;
                        long start = before;
                        try
                        {
                            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            {
                                SuperImage.Extract(stream, row.Partition, file, new Progress(done =>
                                {
                                    report(start + done, total);
                                    ui(delegate { row.Progress = size > 0 ? done * 100.0 / size : 100; });
                                }), token);
                            }
                            log("  " + row.Name + ": " + ByteSize.Format(size) + " -> " + path);
                            ui(delegate
                            {
                                row.Progress = 100;
                                row.Current = SuperRow.Stage.Saved;
                            });
                        }
                        catch (Exception e)
                        {
                            deleteQuietly(path);
                            if (e is OperationCanceledException)
                            {
                                ui(delegate { row.Current = SuperRow.Stage.Skipped; });
                                throw;
                            }
                            failed.Add(row.Name + ": " + e.Message);
                            log("  " + row.Name + " failed: " + e.Message);
                            ui(delegate { row.Current = SuperRow.Stage.Failed; });
                        }
                        before += size;
                    }
                }
                lastOutput = folder;
                if (failed.Count > 0)
                    throw new IOException(string.Join("\n", failed));
                return string.Format(Properties.Resources.images_extract_done, chosen.Count, folder);
            }, new List<string>());
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

        static void openInExplorer(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", Core.Adb.AdbCommand.WindowsArgument(folder)) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
