using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using FastbootEnhance.Core.Adb;
using FastbootEnhance.Core.Fastboot;
using FastbootEnhance.Core.Images;
using FastbootEnhance.Core.Payload;

namespace FastbootEnhance
{
    /// <summary>
    /// The Root page: takes the stock boot or init_boot image (or pulls it out of an OTA),
    /// sends it to the phone for Magisk, KernelSU or APatch to patch, brings the patched image
    /// back, checks it, and flashes it to the right partition from the bootloader.
    /// </summary>
    static class RootUI
    {
        static MainWindow W => MainWindow.THIS;

        static readonly TimeSpan FastbootWait = TimeSpan.FromMinutes(2);

        // The work folder keeps the stock image (to undo the root) and the patched one.
        static string folder;
        static string stockPath;
        static BootImageAnalysis stock;
        static string partition;
        static string adbSerial;
        static DateTime sentAt;
        static string patchedPath;
        static volatile bool busy;

        static void log(string line) => LogStore.Append("root: " + line);

        static RootTool tool => W.root_tool_kernelsu.IsChecked == true ? RootTool.KernelSU
            : W.root_tool_apatch.IsChecked == true ? RootTool.APatch : RootTool.Magisk;

        public static void init()
        {
            W.root_pick.Click += delegate { pick(); };
            W.root_push.Click += delegate { push(); };
            W.root_pull.Click += delegate { pull(); };
            W.root_flash.Click += delegate { flash(); };
            W.root_boot_once.Click += delegate { bootOnce(); };
            W.root_open_folder.Click += delegate { openFolder(); };
            foreach (var radio in new[] { W.root_tool_magisk, W.root_tool_kernelsu, W.root_tool_apatch })
                radio.Checked += delegate { toolChanged(); };
            refresh();
        }

        /// <summary>A different app may patch a different image: start again from step 1.</summary>
        static void toolChanged()
        {
            if (W.root_pick == null)
                return; // still loading
            stockPath = null;
            stock = null;
            partition = null;
            patchedPath = null;
            W.root_image_text.Text = "";
            W.root_push_text.Text = "";
            W.root_pull_text.Text = "";
            W.root_flash_text.Text = "";
            refresh();
        }

        static void refresh()
        {
            string phoneName = partition != null ? RootHelper.PhoneName(partition) : "fbstudio_….img";
            W.root_step2_note.Text = string.Format(Properties.Resources.root_step2_note, phoneName);
            string how = tool == RootTool.KernelSU ? Properties.Resources.root_how_kernelsu
                : tool == RootTool.APatch ? Properties.Resources.root_how_apatch
                : Properties.Resources.root_how_magisk;
            W.root_how.Text = string.Format(how, phoneName);
            W.root_step4_note.Text = string.Format(Properties.Resources.root_step4_note, partition ?? "boot / init_boot");

            W.root_pick.IsEnabled = !busy;
            foreach (var radio in new[] { W.root_tool_magisk, W.root_tool_kernelsu, W.root_tool_apatch })
                radio.IsEnabled = !busy;
            W.root_push.IsEnabled = !busy && stockPath != null;
            W.root_pull.IsEnabled = !busy && adbSerial != null;
            W.root_flash.IsEnabled = !busy && patchedPath != null;
            W.root_boot_once.Visibility = partition == "boot" ? Visibility.Visible : Visibility.Collapsed;
            W.root_boot_once.IsEnabled = !busy && patchedPath != null && partition == "boot";
            W.root_open_folder.IsEnabled = folder != null;
        }

        /// <summary>Runs work in the background with the page locked, then done on the UI thread.</summary>
        static void run(Action work, Action done)
        {
            busy = true;
            refresh();
            Helper.offloadAndRun(delegate
            {
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    log("failed: " + e);
                }
            }, delegate
            {
                busy = false;
                done();
                refresh();
            });
        }

        // ---------------------------------------------------------------- 1. the stock image

        static void pick()
        {
            Helper.fileSelect(new Helper.PathSelectCallback(delegate (string path)
            {
                RootTool chosen = tool;
                string error = null, note = null;
                string newFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Fastboot Studio", "Root", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                string image = null, imagePartition = null;
                BootImageAnalysis analysis = null;
                W.root_image_text.Text = Properties.Resources.root_picking;

                run(delegate
                {
                    try
                    {
                        ImageInfo info = ImageProbe.Identify(path);
                        if (info.Kind == ImageKind.Payload || info.Kind == ImageKind.Zip)
                        {
                            using (PayloadFile payload = PayloadFile.Open(path))
                            {
                                bool hasInitBoot = payload.Partitions.Any(p => p.Name == "init_boot");
                                imagePartition = RootHelper.PartitionFor(chosen, hasInitBoot);
                                if (!payload.Partitions.Any(p => p.Name == imagePartition))
                                {
                                    error = Properties.Resources.root_no_image_in_ota;
                                    return;
                                }
                                if (imagePartition == "init_boot")
                                    note = string.Format(Properties.Resources.root_needs_init_boot, chosen);
                                ExtractionReport report = PayloadExtractor.ExtractAll(payload, newFolder, new[] { imagePartition });
                                PartitionResult result = report.Results.Single();
                                if (!result.Succeeded)
                                {
                                    error = result.Error;
                                    return;
                                }
                                image = result.OutputPath;
                            }
                        }
                        else
                        {
                            analysis = BootImageAnalysis.Analyze(path);
                            if (analysis.Kind != BootImageKind.Bootable && analysis.Kind != BootImageKind.NoKernel)
                            {
                                error = Properties.Resources.root_not_boot;
                                return;
                            }
                            imagePartition = analysis.Kind == BootImageKind.NoKernel ? "init_boot" : "boot";
                            if (chosen == RootTool.APatch && imagePartition != "boot")
                            {
                                error = Properties.Resources.root_apatch_kernel;
                                return;
                            }
                            Directory.CreateDirectory(newFolder);
                            image = Path.Combine(newFolder, imagePartition + ".img");
                            File.Copy(path, image, true);
                        }
                        analysis = BootImageAnalysis.Analyze(image);
                        if (analysis.Root != RootKind.None)
                            error = string.Format(Properties.Resources.root_already_rooted,
                                BootFlashCheck.RootName(analysis.Root, analysis.Kernel?.SuSFS ?? false));
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                }, delegate
                {
                    if (error != null)
                    {
                        log("image " + path + " not used: " + error);
                        W.root_image_text.Text = "";
                        ThemedDialog.Show(error, Properties.Resources.nav_root, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    folder = newFolder;
                    stockPath = image;
                    stock = analysis;
                    partition = imagePartition;
                    adbSerial = null;
                    patchedPath = null;
                    W.root_push_text.Text = "";
                    W.root_pull_text.Text = "";
                    W.root_flash_text.Text = "";
                    W.root_image_text.Text = describe(partition, analysis) + (note != null ? "\n" + note : "");
                    log("stock " + partition + " ready in " + folder + " (from " + path + ")");
                });
            }), "Boot image or OTA|*.img;*.bin;*.zip|All files|*.*");
        }

        /// <summary>"init_boot.img · Android 14 · 2024-09 · android14-6.1 · stock".</summary>
        static string describe(string name, BootImageAnalysis image, bool patched = false)
        {
            List<string> parts = new List<string> { name + ".img" };
            if (image.OsVersion != null)
                parts.Add("Android " + image.OsVersion);
            if (image.PatchLevel != null)
                parts.Add(image.PatchLevel);
            if (image.Kernel?.Kmi != null)
                parts.Add(image.Kernel.Kmi);
            else if (image.Kernel?.Release != null)
                parts.Add(image.Kernel.Release);
            parts.Add(image.Root == RootKind.None ? Properties.Resources.root_image_stock
                : string.Format(patched ? Properties.Resources.root_image_patched : Properties.Resources.root_image_rooted,
                    BootFlashCheck.RootName(image.Root, image.Kernel?.SuSFS ?? false)));
            return Helper.ltr(string.Join("  ·  ", parts));
        }

        // ---------------------------------------------------------------- 2. to the phone

        /// <summary>The one phone running Android over adb, or null.</summary>
        static AdbDevice findPhone()
        {
            Adb.Result list = Adb.Run("devices -l", Adb.ShortCommand);
            return AdbDevice.ParseList(list.Output).FirstOrDefault(d => d.State == "device");
        }

        static void push()
        {
            string local = stockPath, remote = RootHelper.PhonePath(partition);
            AdbDevice phone = null;
            DateTime phoneNow = DateTime.MinValue;
            string error = null;
            run(delegate
            {
                phone = findPhone();
                if (phone == null)
                {
                    error = Properties.Resources.root_no_adb;
                    return;
                }
                // The phone's own clock: patched files are looked for from this time on.
                Adb.Result clock = Adb.Run(AdbCommand.Shell(phone.Serial, "date +%s"), Adb.ShortCommand);
                long seconds;
                phoneNow = clock.Succeeded && long.TryParse(clock.Output.Trim(), out seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    : DateTime.UtcNow;
                Adb.Result sent = Adb.Run(AdbCommand.Push(phone.Serial, local, remote), Fastboot.LongCommand);
                if (!sent.Succeeded)
                    error = string.Format(Properties.Resources.root_push_failed, sent.Problem);
            }, delegate
            {
                if (error != null)
                {
                    log(error);
                    W.root_push_text.Text = error;
                    return;
                }
                adbSerial = phone.Serial;
                sentAt = phoneNow.AddMinutes(-1);
                patchedPath = null;
                W.root_push_text.Text = string.Format(Properties.Resources.root_pushed, Helper.ltr(remote), phone.Model ?? phone.Serial);
                W.root_pull_text.Text = "";
                log("sent " + local + " to " + phone.Serial + ":" + remote);
            });
        }

        // ---------------------------------------------------------------- 3. back from the phone

        static void pull()
        {
            string serial = adbSerial;
            RootTool chosen = tool;
            PatchedImage found = null;
            BootImageAnalysis patched = null;
            string local = null, error = null;
            List<PatchProblem> problems = null;
            run(delegate
            {
                Adb.Result listing = Adb.Run(AdbCommand.Shell(serial, RootHelper.ListPatchedCommand), Adb.ShortCommand);
                found = RootHelper.FindNewest(listing.Output, sentAt);
                if (found == null)
                {
                    error = Properties.Resources.root_no_patched;
                    return;
                }
                local = Path.Combine(folder, found.Name);
                Adb.Result pulled = Adb.Run(AdbCommand.Pull(serial, found.Path, local), Fastboot.LongCommand);
                if (!pulled.Succeeded || !File.Exists(local))
                {
                    error = pulled.Problem;
                    return;
                }
                patched = BootImageAnalysis.Analyze(local);
                problems = RootHelper.Verify(stock, patched, chosen);
            }, delegate
            {
                if (error != null)
                {
                    log("no patched image: " + error);
                    W.root_pull_text.Text = error;
                    return;
                }
                log("got " + found.Path + " -> " + local + (problems.Count > 0 ? " (" + string.Join(", ", problems) + ")" : ""));
                if (problems.Count > 0)
                {
                    string text = Properties.Resources.root_patch_problems + "\n\n" + string.Join("\n", problems.Select(p => "• " + (
                        p == PatchProblem.NotRooted ? Properties.Resources.root_patch_not_rooted
                        : p == PatchProblem.DifferentKind ? Properties.Resources.root_patch_kind
                        : Properties.Resources.root_patch_kernel)));
                    ThemedDialog.Show(text, Properties.Resources.nav_root, MessageBoxButton.OK, MessageBoxImage.Warning);
                    W.root_pull_text.Text = text;
                    return;
                }
                patchedPath = local;
                W.root_pull_text.Text = string.Format(Properties.Resources.root_pulled, Helper.ltr(found.Name), describe(partition, patched, true));
            });
        }

        // ---------------------------------------------------------------- 4. flash

        /// <summary>Restarts the phone to the bootloader and waits for it there; returns its fastboot serial.</summary>
        static string toBootloader(string serial)
        {
            List<string> before = Fastboot.ListSerials();
            if (before.Count == 0 && serial != null)
                Adb.Run(AdbCommand.Reboot(serial, "bootloader"), Adb.ShortCommand);
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < FastbootWait)
            {
                List<string> serials = Fastboot.ListSerials();
                if (serials.Count > 0)
                    return serials.Contains(serial) ? serial : serials[0];
                Thread.Sleep(2000);
            }
            return null;
        }

        static void flash()
        {
            string image = patchedPath, target = partition, serial = adbSerial;
            if (!ThemedDialog.ConfirmTyped(string.Format(Properties.Resources.root_confirm_flash, serial, Path.GetFileName(image), target),
                    Properties.Resources.nav_root, target))
                return;

            string fastbootSerial = null, error = null;
            W.root_flash_text.Text = Properties.Resources.root_waiting_fastboot;
            run(delegate
            {
                fastbootSerial = toBootloader(serial);
                if (fastbootSerial == null)
                {
                    error = string.Format(Properties.Resources.root_no_fastboot, Helper.ltr(image));
                    return;
                }
                W.Dispatcher.Invoke(delegate { W.root_flash_text.Text = string.Format(Properties.Resources.root_flashing, target); });
                string output;
                // No slot suffix: fastboot writes the active slot's partition.
                int? code = Fastboot.Run(fastbootSerial, "flash " + target + " " + AdbCommand.WindowsArgument(image), Fastboot.LongCommand, out output);
                log("fastboot flash " + target + " " + image + " -> " + code + "\n" + output.Trim());
                if (code != 0)
                    error = string.Format(Properties.Resources.root_flash_failed, output.Trim());
            }, delegate
            {
                if (error != null)
                {
                    W.root_flash_text.Text = error;
                    ThemedDialog.Show(error, Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (Helper.confirm(string.Format(Properties.Resources.root_flashed, target), Properties.Resources.nav_root))
                {
                    string output;
                    Fastboot.Run(fastbootSerial, "reboot", Fastboot.ShortCommand, out output);
                }
                W.root_flash_text.Text = Properties.Resources.root_done;
            });
        }

        /// <summary>"fastboot boot": the patched boot image runs once, from memory, without being written.</summary>
        static void bootOnce()
        {
            string image = patchedPath, serial = adbSerial, error = null;
            W.root_flash_text.Text = Properties.Resources.root_waiting_fastboot;
            run(delegate
            {
                string fastbootSerial = toBootloader(serial);
                if (fastbootSerial == null)
                {
                    error = string.Format(Properties.Resources.root_no_fastboot, Helper.ltr(image));
                    return;
                }
                W.Dispatcher.Invoke(delegate { W.root_flash_text.Text = Properties.Resources.root_booting; });
                string output;
                int? code = Fastboot.Run(fastbootSerial, "boot " + AdbCommand.WindowsArgument(image), Fastboot.LongCommand, out output);
                log("fastboot boot " + image + " -> " + code + "\n" + output.Trim());
                if (code != 0)
                    error = string.Format(Properties.Resources.root_boot_failed, output.Trim());
            }, delegate
            {
                W.root_flash_text.Text = error ?? Properties.Resources.root_booted;
            });
        }

        static void openFolder()
        {
            if (folder == null)
                return;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", AdbCommand.WindowsArgument(folder)) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                log("cannot open " + folder + ": " + e.Message);
            }
        }
    }
}
