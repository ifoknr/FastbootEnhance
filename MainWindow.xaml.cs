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

            Closed += delegate
            {
                PayloadUI.cancelRunningWork();
                PayloadUI.closeCurrent();
                clearStagingDirectories();
            };
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
