using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow THIS;
        const string version = "2.0.0";

        public MainWindow()
        {
            InitializeComponent();
            THIS = this;
            FlowDirection = Languages.Flow;
            ThemedWindow.Attach(this);

            clearStagingDirectories();

            PayloadUI.init();
            FastbootUI.init();
            BackupUI.init();
            ImageToolsUI.init();
            SuperUI.init();

            Title += " v" + version;

            // "FastbootStudio.exe ota.zip", or a package dropped onto the exe, opens it straight away.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                string path = args[1];
                Loaded += delegate
                {
                    // A payload or an OTA zip goes to the Payload Dumper; any other image to Image Tools.
                    if (looksLikePayload(path))
                    {
                        PayloadUI.openFromPath(path);
                    }
                    else
                    {
                        main_tabs.SelectedItem = images_tab;
                        ImageToolsUI.openFromPath(path);
                    }
                };
            }

            wireLogs();
            wireLanguages();

            // Closing mid-flash kills the worker between partitions and can leave the phone
            // half written, so it has to be a deliberate choice.
            Closing += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                string question = FastbootUI.flashing ? Properties.Resources.confirm_close_flashing
                    : PayloadUI.extracting || ImageToolsUI.busy || SuperUI.busy ? Properties.Resources.confirm_close_extracting
                    : null;
                if (question == null)
                    return;

                if (ThemedDialog.Show(question, Properties.Resources.confirm_title,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    e.Cancel = true;
            };

            Closed += delegate
            {
                FastbootUI.abortFlash();
                BackupUI.shutdown();
                ImageToolsUI.shutdown();
                SuperUI.shutdown();
                PayloadUI.cancelRunningWork();
                PayloadUI.closeCurrent();
                clearStagingDirectories();
            };
        }

        /// <summary>One button per language; the one in use is highlighted.</summary>
        void wireLanguages()
        {
            Languages.Option current = Languages.CurrentOption;
            foreach (Languages.Option option in Languages.All)
            {
                Languages.Option choice = option;
                System.Windows.Controls.Button button = new System.Windows.Controls.Button
                {
                    Content = choice.NativeName,
                    Tag = choice.Code,
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(0, 0, 8, 8),
                    Style = (Style)FindResource(choice == current ? "AccentButton" : "GhostButton"),
                };
                button.Click += delegate
                {
                    if (choice == Languages.CurrentOption)
                        return;
                    if (FastbootUI.flashing || PayloadUI.extracting)
                    {
                        ThemedDialog.Show(Properties.Resources.language_busy);
                        return;
                    }
                    Languages.Save(choice.Code);
                    if (ThemedDialog.Show(Properties.Resources.language_restart, choice.NativeName,
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                        App.Restart();
                };
                language_buttons.Children.Add(button);
            }
        }

        static bool looksLikePayload(string path)
        {
            try
            {
                byte[] magic = new byte[4];
                using (FileStream file = File.OpenRead(path))
                {
                    if (file.Read(magic, 0, 4) < 4)
                        return true;
                }
                bool payload = magic[0] == 'C' && magic[1] == 'r' && magic[2] == 'A' && magic[3] == 'U';
                bool zip = magic[0] == 0x50 && magic[1] == 0x4B;
                return payload || zip;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        const int MaxLogBoxChars = 400000;

        /// <summary>The Logs page shows the whole session; the Flash page shows the same feed.</summary>
        void wireLogs()
        {
            log_text.Text = LogStore.Snapshot();

            LogStore.LineAdded += line =>
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    appendTo(log_text, line);
                    if (FastbootUI.flashing)
                        appendTo(flash_log, line);
                }));
            };

            LogStore.Cleared += () => Dispatcher.BeginInvoke(new Action(delegate
            {
                log_text.Clear();
                forgetLength(log_text);
            }));

            log_clear.Click += delegate { LogStore.Clear(); };
            log_copy.Click += delegate
            {
                try
                {
                    Clipboard.SetText(LogStore.Snapshot());
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Another app is holding the clipboard; nothing useful to do about it.
                }
            };
        }

        /// <summary>
        /// The length of each log box's text. Reading TextBox.Text copies the whole document,
        /// so the length is tracked here instead of being read on every line.
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<System.Windows.Controls.TextBox, int> logLengths =
            new System.Collections.Generic.Dictionary<System.Windows.Controls.TextBox, int>();

        /// <summary>Call after clearing a log box.</summary>
        public static void forgetLength(System.Windows.Controls.TextBox box)
        {
            logLengths.Remove(box);
        }

        /// <summary>Appends a line, keeping the box bounded.</summary>
        static void appendTo(System.Windows.Controls.TextBox box, string line)
        {
            int known;
            int length = logLengths.TryGetValue(box, out known) ? known : box.Text.Length;
            if (length > MaxLogBoxChars)
            {
                // Measured again here: the box may have been cleared since the length was stored.
                string text = box.Text;
                string kept = text.Length > MaxLogBoxChars / 2 ? text.Substring(text.Length - MaxLogBoxChars / 2) : text;
                box.Text = kept;
                length = kept.Length;
            }
            box.AppendText(line + "\n");
            logLengths[box] = length + line.Length + 1;
            box.ScrollToEnd();
        }

        /// <summary>
        /// Removes anything a previous run left behind. These directories only ever hold
        /// images on their way to the device, so they are safe to drop on start and on exit.
        /// </summary>
        static void clearStagingDirectories()
        {
            foreach (string path in new[] { PayloadUI.PAYLOAD_TMP, FastbootUI.PAYLOAD_TMP })
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                }
                catch (DirectoryNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Opens a link in the user's browser. Modern .NET will not launch a URL unless the
        /// shell is asked to handle it, so UseShellExecute has to be set.
        /// </summary>
        static void openInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                ThemedDialog.Show(e.Message);
            }
        }

        private void Telegram_Click(object sender, RoutedEventArgs e)
        {
            openInBrowser("https://t.me/IFOKNR1");
        }

        private void GitHub_Click(object sender, RoutedEventArgs e)
        {
            openInBrowser("https://github.com/ifoknr");
        }

        /// <summary>The MIT licence of the original project, shipped inside the app.</summary>
        private void License_Click(object sender, RoutedEventArgs e)
        {
            string text;
            using (Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream("LICENSE.txt"))
            using (StreamReader reader = new StreamReader(stream))
                text = reader.ReadToEnd();
            ThemedDialog.Show(text.Trim(), Properties.Resources.about_license);
        }

        private void Thread_Click(object sender, RoutedEventArgs e)
        {
            openInBrowser("https://www.akr-developers.com/d/506");
        }

        private void OSS_Click(object sender, RoutedEventArgs e)
        {
            openInBrowser("https://github.com/libxzr/FastbootEnhance");
        }
    }
}
