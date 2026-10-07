using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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

        static Mutex singleInstanceWatcher;

        public MainWindow()
        {
            InitializeComponent();
            THIS = this;

            bool createdNew;
            singleInstanceWatcher = new Mutex(false, "FastbootEnhance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(Properties.Resources.program_already_running, Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            clearStagingDirectories();

            PayloadUI.init();
            FastbootUI.init();

            Title += " v" + version;

            // "FastbootEnhance.exe ota.zip", or a package dropped onto the exe, opens it straight away.
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                string path = args[1];
                Loaded += delegate { PayloadUI.openFromPath(path); };
            }

            wireLogs();

            // Closing mid-flash kills the worker between partitions and can leave the phone
            // half written, so it has to be a deliberate choice.
            Closing += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!FastbootUI.flashing)
                    return;

                if (MessageBox.Show(Properties.Resources.confirm_close_flashing, Properties.Resources.confirm_title,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    e.Cancel = true;
            };

            Closed += delegate
            {
                PayloadUI.cancelRunningWork();
                PayloadUI.closeCurrent();
                clearStagingDirectories();
            };
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

            LogStore.Cleared += () => Dispatcher.BeginInvoke(new Action(delegate { log_text.Clear(); }));

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

        static void appendTo(System.Windows.Controls.TextBox box, string line)
        {
            if (box.Text.Length > MaxLogBoxChars)
                box.Text = box.Text.Substring(box.Text.Length - MaxLogBoxChars / 2);
            box.AppendText(line + "\n");
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
                MessageBox.Show(e.Message);
            }
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
