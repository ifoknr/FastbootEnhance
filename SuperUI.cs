using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using FastbootEnhance.Core;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance
{
    /// <summary>
    /// The Build Super page: puts partition images together into a super image the way AOSP's
    /// lpmake does, from a standard layout or from the layout of an existing super image, then
    /// reads the result back to check it. Work runs on one worker thread.
    /// </summary>
    static class SuperUI
    {
        static MainWindow W => MainWindow.THIS;

        static SuperPlan plan = SuperPlan.Create(SuperSlotMode.VirtualAB, 0);

        /// <summary>The super image whose layout was imported, and where its unpacked images were found.</summary>
        static SuperImage imported;
        static string importedFrom;
        static string importedFolder;

        /// <summary>What the import found, shown under the layout choice while it is in use.</summary>
        static string importedSource;

        static readonly ObservableCollection<SuperBuildRow> rows = new ObservableCollection<SuperBuildRow>();

        public static volatile bool busy;
        static CancellationTokenSource cancel;
        static volatile Thread worker;
        static string lastOutput;

        /// <summary>True while the page fills its own fields, so their change events are ignored.</summary>
        static bool updating;

        static readonly Regex SplitSuffix = new Regex(@"([._]sparsechunk)?\.\d+$", RegexOptions.IgnoreCase);
        static readonly Regex ImageSuffix = new Regex(@"(\.(img|simg|raw|bin))+$", RegexOptions.IgnoreCase);
        const string ImageFilter = "Android images|*.img;*.simg;*.raw;*.bin;*sparsechunk*;*.img.*|All files|*.*";

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
            W.super_partitions.ItemsSource = rows;

            W.super_mode_vab.Checked += delegate { modeChosen(SuperSlotMode.VirtualAB); };
            W.super_mode_ab.Checked += delegate { modeChosen(SuperSlotMode.AB); };
            W.super_mode_single.Checked += delegate { modeChosen(SuperSlotMode.Single); };
            W.super_mode_imported.Checked += delegate { modeChosen(SuperSlotMode.Imported); };

            W.super_size.TextChanged += delegate { sizeChanged(); };
            W.super_group_size.TextChanged += delegate { groupSizeChanged(); };
            W.super_group.LostFocus += delegate { groupNameChanged(); };
            W.super_group.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    groupNameChanged();
                }
            };

            W.super_import.Click += delegate { importSuper(); };
            W.super_read_phone.Click += delegate { readFromPhone(); };
            W.super_add.Click += delegate { addImages(); };
            W.super_add_folder.Click += delegate { addFolder(); };
            W.super_remove.Click += delegate { removeSelected(); };
            W.super_clear.Click += delegate { clear(); };
            W.super_partitions.MouseDoubleClick += delegate { pickForSelected(); };
            W.super_partitions.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Delete && !busy)
                {
                    e.Handled = true;
                    removeSelected();
                }
            };

            W.super_partitions.DragEnter += dragOver;
            W.super_partitions.DragOver += dragOver;
            W.super_partitions.Drop += delegate (object sender, DragEventArgs e)
            {
                string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped == null || busy)
                    return;
                foreach (string folder in dropped.Where(Directory.Exists))
                    addFolder(folder);
                List<string> files = dropped.Where(File.Exists).ToList();
                if (files.Count > 0)
                    addFiles(files);
            };

            W.super_build.Click += delegate { build(); };
            W.super_cancel.Click += delegate
            {
                CancellationTokenSource source = cancel;
                if (source != null)
                    source.Cancel();
            };
            W.super_open_folder.Click += delegate { openInExplorer(lastOutput); };

            showAll(true);
            setBusy(false);
        }

        /// <summary>Stops a running build when the window closes.</summary>
        public static void shutdown()
        {
            CancellationTokenSource source = cancel;
            if (source != null)
                source.Cancel();
            Thread running = worker;
            if (running != null)
                running.Join(TimeSpan.FromSeconds(10));
        }

        static void dragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !busy ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // ------------------------------------------------------------------ layout

        static void modeChosen(SuperSlotMode mode)
        {
            if (updating || busy || mode == plan.Mode)
                return;

            if (mode == SuperSlotMode.Imported)
            {
                if (imported == null)
                    return;
                List<SuperPlanPartition> had = plan.Partitions.Where(p => p.HasImage).ToList();
                SuperPlan fresh = SuperPlan.FromImage(imported);
                if (importedFolder != null)
                    fresh.AttachFolder(importedFolder);
                foreach (SuperPlanPartition partition in had)
                    tryAdd(fresh, partition.ImagePath, partition.Name);
                plan = fresh;
            }
            else
            {
                plan.Arrange(mode, W.super_group.Text);
                applyGroupSizeText();
            }
            log("build super: layout " + plan.Mode);
            showAll(true);
        }

        static void tryAdd(SuperPlan target, string path, string name)
        {
            try
            {
                target.AddImage(path, name);
            }
            catch (Exception e)
            {
                log("build super: " + path + ": " + e.Message);
            }
        }

        /// <summary>Bytes from a field: digits (spaces and commas ignored) or 0x hex. 0 when empty or invalid.</summary>
        static long parseBytes(string text)
        {
            string value = (text ?? "").Trim().Replace(" ", "").Replace(",", "");
            long result;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out result) && result > 0 ? result : 0;
            return long.TryParse(value, out result) && result > 0 ? result : 0;
        }

        static void sizeChanged()
        {
            if (updating || busy)
                return;
            plan.DeviceSize = parseBytes(W.super_size.Text);
            plan.UpdateAutoGroupSize();
            showAll(false);
        }

        static void groupSizeChanged()
        {
            if (updating || busy)
                return;
            applyGroupSizeText();
            showAll(false);
        }

        /// <summary>An empty group size field means automatic (or, for an imported layout, as imported).</summary>
        static void applyGroupSizeText()
        {
            long size = parseBytes(W.super_group_size.Text);
            if (size > 0)
            {
                plan.GroupSizeAuto = false;
                foreach (SuperPlanGroup group in plan.Groups)
                    group.MaximumSize = (ulong)size;
            }
            else if (plan.Mode == SuperSlotMode.Imported && imported != null)
            {
                foreach (SuperPlanGroup group in plan.Groups)
                {
                    SuperGroup original = imported.Groups.FirstOrDefault(g => g.Name == group.Name);
                    if (original != null)
                        group.MaximumSize = original.MaximumSize;
                }
            }
            else
            {
                plan.GroupSizeAuto = true;
                plan.UpdateAutoGroupSize();
            }
        }

        static void groupNameChanged()
        {
            if (updating || busy || plan.Mode == SuperSlotMode.Imported)
                return;
            string name = W.super_group.Text.Trim();
            if (name.Length == 0 || name == plan.GroupBase)
                return;
            plan.Arrange(plan.Mode, name);
            applyGroupSizeText();
            showAll(false);
        }

        // ------------------------------------------------------------------ showing

        static string modeLabel(SuperPlan of)
        {
            if (of.VirtualAB)
                return Properties.Resources.super_mode_vab;
            return of.Slotted ? Properties.Resources.super_mode_ab : Properties.Resources.super_mode_single;
        }

        /// <summary>Redraws the page; <paramref name="fields"/> also rewrites the text boxes.</summary>
        static void showAll(bool fields)
        {
            updating = true;
            try
            {
                W.super_mode_imported.Visibility = imported != null ? Visibility.Visible : Visibility.Collapsed;
                W.super_mode_vab.IsChecked = plan.Mode == SuperSlotMode.VirtualAB;
                W.super_mode_ab.IsChecked = plan.Mode == SuperSlotMode.AB;
                W.super_mode_single.IsChecked = plan.Mode == SuperSlotMode.Single;
                W.super_mode_imported.IsChecked = plan.Mode == SuperSlotMode.Imported;
                W.super_mode_note.Text = plan.Mode == SuperSlotMode.VirtualAB ? Properties.Resources.super_mode_note_vab
                    : plan.Mode == SuperSlotMode.AB ? Properties.Resources.super_mode_note_ab
                    : plan.Mode == SuperSlotMode.Single ? Properties.Resources.super_mode_note_single
                    : importedSource ?? string.Format(Properties.Resources.super_mode_note_imported, Helper.ltr(Path.GetFileName(importedFrom ?? "")));

                W.super_group.IsEnabled = plan.Mode != SuperSlotMode.Imported;
                if (fields)
                {
                    W.super_size.Text = plan.DeviceSize > 0 ? plan.DeviceSize.ToString() : "";
                    W.super_group.Text = plan.GroupBase;
                    if (plan.GroupSizeAuto || plan.Mode == SuperSlotMode.Imported)
                        W.super_group_size.Text = "";
                }
            }
            finally
            {
                updating = false;
            }

            W.super_size_human.Text = plan.DeviceSize > 0 ? Helper.byte2AUnit(plan.DeviceSize) : "";
            SuperPlanGroup first = plan.Groups.FirstOrDefault();
            string groupSize = first == null ? "" : first.MaximumSize == 0 ? "∞" : Helper.byte2AUnit((long)first.MaximumSize);
            W.super_group_size_human.Text = W.super_group_size.Text.Trim().Length == 0
                ? string.Format(Properties.Resources.super_auto_value, groupSize) : groupSize;

            rows.Clear();
            foreach (SuperPlanPartition partition in plan.Partitions)
                rows.Add(new SuperBuildRow(partition));
            W.super_partitions_empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SuperPlanCheck check = plan.Check();
            if (plan.DeviceSize > 0)
            {
                W.super_usage.Value = Math.Min(100, check.UsedEnd * 100.0 / plan.DeviceSize);
                W.super_usage.Foreground = Palette.Get(check.UsedEnd > plan.DeviceSize ? "Danger" : "Accent");
                W.super_usage_text.Text = string.Format(Properties.Resources.super_usage, Helper.byte2AUnit(check.UsedEnd),
                    Helper.byte2AUnit(plan.DeviceSize), Helper.byte2AUnit(Math.Max(0, plan.DeviceSize - check.UsedEnd)));
            }
            else
            {
                W.super_usage.Value = 0;
                W.super_usage_text.Text = Properties.Resources.super_usage_none;
            }

            // A fresh page is not "wrong" yet: problems show once something has been entered.
            bool started = plan.DeviceSize > 0 || plan.Partitions.Any(p => p.HasImage);
            List<string> problems = check.Problems.Select(problemText).Distinct().ToList();
            W.super_problems.Text = string.Join("\n", problems.Select(p => "•  " + p));
            W.super_problems_box.Visibility = started && problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            W.super_build.IsEnabled = check.CanBuild && !busy;
        }

        static string problemText(SuperProblem problem)
        {
            switch (problem.Kind)
            {
                case SuperProblemKind.NoSize: return Properties.Resources.super_problem_no_size;
                case SuperProblemKind.SizeNotAligned: return Properties.Resources.super_problem_size_aligned;
                case SuperProblemKind.MetadataNotAligned: return Properties.Resources.super_problem_metadata;
                case SuperProblemKind.MetadataTooLarge: return Properties.Resources.super_problem_metadata_large;
                case SuperProblemKind.NoImages: return Properties.Resources.super_problem_no_images;
                case SuperProblemKind.BadName: return string.Format(Properties.Resources.super_problem_bad_name, Helper.ltr(problem.Subject));
                case SuperProblemKind.DuplicateName: return string.Format(Properties.Resources.super_problem_duplicate, Helper.ltr(problem.Subject));
                case SuperProblemKind.UnknownGroup: return string.Format(Properties.Resources.super_problem_unknown_group, Helper.ltr(problem.Subject));
                case SuperProblemKind.GroupFull:
                    return string.Format(Properties.Resources.super_problem_group_full, Helper.ltr(problem.Subject),
                        Helper.byte2AUnit(problem.Need), Helper.byte2AUnit(problem.Have));
                case SuperProblemKind.DoesNotFit:
                    return string.Format(Properties.Resources.super_problem_does_not_fit,
                        Helper.byte2AUnit(problem.Need), Helper.byte2AUnit(problem.Have));
                default:
                    return problem.ToString();
            }
        }

        static void setBusy(bool value)
        {
            busy = value;
            W.super_editor.IsEnabled = !value;
            W.super_import.IsEnabled = !value;
            W.super_read_phone.IsEnabled = !value;
            W.super_format_panel.IsEnabled = !value;
            W.super_verify.IsEnabled = !value;
            W.super_build.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            W.super_cancel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            W.super_build.IsEnabled = !value && plan.Check().CanBuild;
            if (value)
                Helper.TaskbarItemHelper.start();
            else
                Helper.TaskbarItemHelper.stop();
        }

        // ------------------------------------------------------------------ images

        static Stream openImage(IList<string> parts)
        {
            if (SparseImage.IsSparse(parts[0]))
                return SparseStream.Open(parts);
            return new FileStream(parts[0], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        }

        static string stem(string path)
        {
            string name = SplitSuffix.Replace(Path.GetFileName(path), "");
            name = ImageSuffix.Replace(name, "");
            return name.Length == 0 ? "super" : name;
        }

        static void importSuper()
        {
            if (busy)
                return;
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog { Filter = ImageFilter };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                // A super cut into pieces by a Qualcomm package (super_1.img ...) is read as one.
                RawProgramImage pieced = RawProgramImage.TryFind(dialog.FileName);
                IList<string> parts = pieced != null ? pieced.Files : SparseConverter.FindParts(dialog.FileName);
                SuperImage read;
                using (Stream stream = pieced != null ? pieced.Open() : openImage(parts))
                {
                    if (!SuperImage.IsSuper(stream))
                    {
                        ThemedDialog.Show(Properties.Resources.super_not_super, Properties.Resources.error,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    read = SuperImage.Read(stream);
                }

                SuperPlan fresh = SuperPlan.FromImage(read);
                string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(parts[0])),
                    (pieced != null ? pieced.Label : stem(parts[0])) + "_unpacked");
                importedFolder = Directory.Exists(folder) ? folder : null;
                int found = importedFolder != null ? fresh.AttachFolder(importedFolder) : 0;

                imported = read;
                importedFrom = parts[0];
                plan = fresh;

                string groups = string.Join(", ", fresh.Groups.Select(g => g.Name));
                string source = string.Format(Properties.Resources.super_import_source, Helper.ltr(Path.GetFileName(parts[0])),
                    modeLabel(fresh), fresh.MetadataSlots, Helper.ltr(groups));
                if (found > 0)
                    source += "\n" + string.Format(Properties.Resources.super_found_images, found, Helper.ltr(Path.GetFileName(folder)));
                importedSource = source;
                log("build super: imported the layout of " + parts[0] + " (" + fresh.Partitions.Count + " partitions, "
                    + fresh.DeviceSize + " bytes" + (found > 0 ? ", " + found + " images from " + folder : "") + ")");

                updating = true;
                W.super_group_size.Text = "";
                updating = false;
                showAll(true);
            }
            catch (Exception e)
            {
                log("build super: import failed: " + e.Message);
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static bool isFilesystem(ImageKind kind)
        {
            return kind == ImageKind.Ext4 || kind == ImageKind.Erofs || kind == ImageKind.F2fs;
        }

        static void addImages()
        {
            if (busy)
                return;
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = ImageFilter };
            if (dialog.ShowDialog() == true)
                addFiles(dialog.FileNames);
        }

        /// <summary>Adds files by name; ones that are not file systems are only added when confirmed.</summary>
        static void addFiles(IEnumerable<string> files)
        {
            // One entry per split image, whichever of its parts was chosen.
            List<string> unique = files.Select(f => SparseConverter.FindParts(f)[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> wanted = new List<string>();
            List<string> odd = new List<string>();
            List<string> errors = new List<string>();
            foreach (string file in unique)
            {
                try
                {
                    ImageInfo info = ImageProbe.Identify(file);
                    if (isFilesystem(info.Kind))
                        wanted.Add(file);
                    else
                        odd.Add(file);
                }
                catch (Exception e)
                {
                    errors.Add(Path.GetFileName(file) + ": " + e.Message);
                }
            }

            if (odd.Count > 0 && Helper.confirm(string.Format(Properties.Resources.super_not_filesystem,
                    string.Join("\n", odd.Select(f => Helper.ltr(Path.GetFileName(f))))), Properties.Resources.nav_super))
                wanted.AddRange(odd);

            foreach (string file in wanted)
            {
                try
                {
                    SuperPlanPartition added = plan.AddImage(file);
                    log("build super: " + added.Name + " <- " + file);
                }
                catch (Exception e)
                {
                    errors.Add(Path.GetFileName(file) + ": " + e.Message);
                }
            }
            showAll(false);
            if (errors.Count > 0)
                ThemedDialog.Show(string.Join("\n", errors), Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        static void addFolder()
        {
            if (busy)
                return;
            Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog { Title = Properties.Resources.super_add_folder };
            if (dialog.ShowDialog() == true)
                addFolder(dialog.FolderName);
        }

        /// <summary>
        /// Fills the partitions named after files in the folder, then adds its other file-system
        /// images; boot, vbmeta and the like are skipped since they are not part of super.
        /// </summary>
        static void addFolder(string folder)
        {
            int attached = plan.AttachFolder(folder);
            List<string> skipped = new List<string>();
            int added = 0;
            HashSet<string> used = new HashSet<string>(plan.Partitions.Where(p => p.HasImage).Select(p => Path.GetFullPath(p.ImagePath)),
                StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> files = Directory.GetFiles(folder)
                .Select(f => SparseConverter.FindParts(f)[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                if (used.Contains(Path.GetFullPath(file)))
                    continue;
                try
                {
                    ImageInfo info = ImageProbe.Identify(file);
                    if (!isFilesystem(info.Kind))
                    {
                        skipped.Add(Path.GetFileName(file));
                        continue;
                    }
                    SuperPlanPartition partition = plan.AddImage(file);
                    log("build super: " + partition.Name + " <- " + file);
                    added++;
                }
                catch (Exception e)
                {
                    skipped.Add(Path.GetFileName(file));
                    log("build super: skipped " + file + ": " + e.Message);
                }
            }
            showAll(false);

            if (attached + added == 0)
            {
                ThemedDialog.Show(Properties.Resources.super_folder_none, Properties.Resources.nav_super,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (skipped.Count > 0)
            {
                log("build super: " + string.Format(Properties.Resources.super_folder_skipped, skipped.Count, string.Join(", ", skipped)));
            }
        }

        static void removeSelected()
        {
            List<SuperBuildRow> chosen = W.super_partitions.SelectedItems.Cast<SuperBuildRow>().ToList();
            if (chosen.Count == 0 || busy)
                return;
            foreach (SuperBuildRow row in chosen)
            {
                // An imported layout keeps its partitions; only the image goes.
                if (plan.Mode == SuperSlotMode.Imported)
                    row.Partition.ClearImage();
                else
                    plan.Remove(row.Partition);
            }
            showAll(false);
        }

        static void clear()
        {
            if (busy || plan.Partitions.Count == 0)
                return;
            if (!Helper.confirm(Properties.Resources.super_clear_confirm, Properties.Resources.nav_super))
                return;
            if (plan.Mode == SuperSlotMode.Imported)
            {
                foreach (SuperPlanPartition partition in plan.Partitions)
                    partition.ClearImage();
            }
            else
            {
                plan.Partitions.Clear();
            }
            showAll(false);
        }

        static void pickForSelected()
        {
            SuperBuildRow row = W.super_partitions.SelectedItem as SuperBuildRow;
            if (row == null || busy)
                return;
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = ImageFilter,
                Title = string.Format(Properties.Resources.super_pick_image, row.Name),
            };
            if (dialog.ShowDialog() != true)
                return;
            try
            {
                plan.AddImage(SparseConverter.FindParts(dialog.FileName)[0], row.Name);
                log("build super: " + row.Name + " <- " + dialog.FileName);
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            showAll(false);
        }

        // ------------------------------------------------------------------ phone

        static readonly Regex SuperSize = new Regex(@"partition-size:super:\s*(0x[0-9a-fA-F]+|\d+)");
        static readonly Regex SlotCount = new Regex(@"slot-count:\s*(\d+)");
        static readonly Regex SnapshotStatus = new Regex(@"snapshot-update-status:\s*\w");

        /// <summary>
        /// Asks the phone in fastboot for the size of super and its slot layout: two slots and a
        /// snapshot status means Virtual A/B.
        /// </summary>
        static void readFromPhone()
        {
            if (busy)
                return;
            W.super_read_phone.IsEnabled = false;
            W.super_progress_text.Text = Properties.Resources.super_reading_phone;

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    List<string> serials = Fastboot.ListSerials();
                    if (serials.Count == 0)
                    {
                        ThemedDialog.Show(Properties.Resources.super_phone_none, Properties.Resources.nav_super,
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    string serial = serials[0];

                    string sizeOutput;
                    Fastboot.Run(serial, "getvar partition-size:super", Fastboot.ShortCommand, out sizeOutput);
                    Match sizeMatch = SuperSize.Match(sizeOutput);
                    long size = 0;
                    if (sizeMatch.Success)
                        size = parseBytes(sizeMatch.Groups[1].Value);
                    if (size <= 0)
                        throw new InvalidOperationException(sizeOutput.Trim());

                    string slotOutput;
                    Fastboot.Run(serial, "getvar slot-count", Fastboot.ShortCommand, out slotOutput);
                    Match slotMatch = SlotCount.Match(slotOutput);
                    int slots = slotMatch.Success ? int.Parse(slotMatch.Groups[1].Value) : 1;

                    string snapshotOutput;
                    Fastboot.Run(serial, "getvar snapshot-update-status", Fastboot.ShortCommand, out snapshotOutput);
                    SuperSlotMode mode = slots >= 2
                        ? (SnapshotStatus.IsMatch(snapshotOutput) ? SuperSlotMode.VirtualAB : SuperSlotMode.AB)
                        : SuperSlotMode.Single;
                    log("build super: " + serial + " has super of " + size + " bytes, " + slots + " slots, layout " + mode);

                    ui(delegate
                    {
                        if (busy)
                            return;
                        if (plan.Mode != SuperSlotMode.Imported && plan.Mode != mode)
                            plan.Arrange(mode, W.super_group.Text);
                        plan.DeviceSize = size;
                        plan.UpdateAutoGroupSize();
                        applyGroupSizeText();
                        showAll(true);
                        string layout = mode == SuperSlotMode.VirtualAB ? Properties.Resources.super_mode_vab
                            : mode == SuperSlotMode.AB ? Properties.Resources.super_mode_ab : Properties.Resources.super_mode_single;
                        ThemedDialog.Show(string.Format(Properties.Resources.super_phone_read, Helper.ltr(serial),
                            Helper.byte2AUnit(size), size, layout), Properties.Resources.nav_super);
                    });
                }
                catch (Exception e)
                {
                    log("build super: reading the phone failed: " + e.Message);
                    ThemedDialog.Show(string.Format(Properties.Resources.super_phone_failed, e.Message),
                        Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    ui(delegate
                    {
                        W.super_read_phone.IsEnabled = !busy;
                        if (!busy)
                            W.super_progress_text.Text = "";
                    });
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // ------------------------------------------------------------------ build

        sealed class Progress : IProgress<long>
        {
            readonly Action<long> action;
            public Progress(Action<long> action) { this.action = action; }
            public void Report(long value) { action(value); }
        }

        static void build()
        {
            if (busy)
                return;
            SuperPlanCheck check = plan.Check();
            if (!check.CanBuild)
                return;

            string firstImage = plan.Partitions.Where(p => p.HasImage).Select(p => p.ImagePath).FirstOrDefault();
            string startIn = importedFrom != null ? Path.GetDirectoryName(Path.GetFullPath(importedFrom))
                : firstImage != null ? Path.GetDirectoryName(Path.GetFullPath(firstImage)) : null;
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "super.img",
                Filter = "super.img|*.img|All files|*.*",
                InitialDirectory = startIn ?? "",
            };
            if (dialog.ShowDialog() != true)
                return;
            string output = Path.GetFullPath(dialog.FileName);

            // Writing over one of the inputs would destroy it halfway through reading it.
            bool isInput = plan.Partitions.Where(p => p.HasImage)
                .SelectMany(p => SparseConverter.FindParts(p.ImagePath))
                .Any(f => string.Equals(Path.GetFullPath(f), output, StringComparison.OrdinalIgnoreCase));
            if (isInput)
            {
                ThemedDialog.Show(Properties.Resources.super_output_is_input, Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool sparse = W.super_format_sparse.IsChecked == true;
            bool verify = W.super_verify.IsChecked == true;
            SuperPlan building = plan;
            long data = Math.Max(1, check.DataBytes);
            long total = verify ? data * 2 : data;
            int partitions = building.Partitions.Count(p => p.HasImage && p.ImageLength > 0);

            CancellationTokenSource source = new CancellationTokenSource();
            cancel = source;
            setBusy(true);
            W.super_progress.Value = 0;
            W.super_progress_text.Text = Properties.Resources.super_building;
            Stopwatch clock = Stopwatch.StartNew();
            log("build super: writing " + output + (sparse ? " (sparse)" : " (raw)") + ", " + building.DeviceSize + " bytes, "
                + string.Join(", ", building.Partitions.Where(p => p.HasImage).Select(p => p.Name)));

            int lastShown = -1;
            Action<long, string> report = (done, what) =>
            {
                int percent = (int)Math.Min(100, done * 100 / total);
                if (percent == lastShown)
                    return;
                lastShown = percent;
                ui(delegate
                {
                    W.super_progress.Value = percent;
                    W.super_progress_text.Text = what + "  ·  " + percent + "%";
                    Helper.TaskbarItemHelper.update(percent);
                });
            };

            Thread thread = new Thread(delegate ()
            {
                string message = null;
                string failure = null;
                bool cancelled = false;
                bool verified = false;
                try
                {
                    SuperLayout layout = building.Build(output, sparse, new Progress(done => report(done, Properties.Resources.super_building)), source.Token);
                    foreach (SuperPlacement place in layout.Placements)
                        log("  " + place.Name + " at " + place.Offset + ", " + ByteSize.Format(place.Allocated));

                    if (verify)
                    {
                        SuperVerifyResult result = building.Verify(output, new Progress(done => report(data + done, Properties.Resources.super_verifying)), source.Token);
                        foreach (KeyValuePair<string, string> hash in result.Sha256)
                            log("  " + hash.Value + "  " + hash.Key);
                        if (!result.Ok)
                        {
                            foreach (string problem in result.Problems)
                                log("  check failed: " + problem);
                            deleteQuietly(output);
                            failure = string.Format(Properties.Resources.super_verify_failed, string.Join("\n", result.Problems.Take(8)));
                        }
                        verified = result.Ok;
                    }

                    if (failure == null)
                    {
                        long length = new FileInfo(output).Length;
                        message = string.Format(verified ? Properties.Resources.super_done : Properties.Resources.super_done_unchecked,
                            Helper.ltr(output), Helper.byte2AUnit(length), partitions);
                        lastOutput = Path.GetDirectoryName(output);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    deleteQuietly(output);
                }
                catch (Exception e)
                {
                    failure = e.Message;
                    deleteQuietly(output);
                }

                string elapsed = clock.Elapsed.TotalSeconds.ToString("F1");
                ui(delegate
                {
                    worker = null;
                    cancel = null;
                    setBusy(false);
                    showAll(false);
                    if (message != null)
                    {
                        W.super_progress.Value = 100;
                        W.super_progress_text.Text = string.Format(Properties.Resources.images_done_in, elapsed);
                        log("build super: done in " + elapsed + " s" + (verified ? ", checked" : ""));
                        ThemedDialog.Done(message);
                    }
                    else if (cancelled)
                    {
                        W.super_progress.Value = 0;
                        W.super_progress_text.Text = "";
                        log("build super: cancelled");
                        ThemedDialog.Show(Properties.Resources.backup_cancelled, Properties.Resources.nav_super,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        W.super_progress.Value = 0;
                        W.super_progress_text.Text = "";
                        Helper.TaskbarItemHelper.error();
                        log("build super: failed: " + failure);
                        ThemedDialog.Show(failure, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            });
            thread.IsBackground = true;
            worker = thread;
            thread.Start();
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
