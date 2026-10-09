using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FastbootEnhance
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Where unexpected errors are written, so a crash leaves something to report.</summary>
        public static readonly string CrashLogPath =
            Path.Combine(Path.GetTempPath(), "FastbootStudio", "crash.log");

        static Mutex singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            Languages.Apply();
            // Before any window or dialog exists, so everything is drawn in the chosen theme.
            Theme.Apply(Resources);

            // Checked before any window exists. Doing it in the window's constructor, as before,
            // left WPF showing a window that had already been told to shut down.
            bool createdNew;
            singleInstance = new Mutex(false, "FastbootStudio", out createdNew);
            if (!createdNew)
            {
                ThemedDialog.Show(global::FastbootEnhance.Properties.Resources.program_already_running,
                    global::FastbootEnhance.Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += onDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                writeCrashLog("background thread", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                writeCrashLog("task", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
            new MainWindow().Show();
        }

        /// <summary>
        /// Starts a fresh copy of the app and closes this one, for a language change. The
        /// single-instance lock is let go first, or the new copy would refuse to start.
        /// </summary>
        public static void Restart()
        {
            Mutex held = singleInstance;
            singleInstance = null;
            if (held != null)
                held.Dispose();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Current.Shutdown();
        }

        void onDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            writeCrashLog("ui thread", e.Exception);
            string text = e.Exception.Message + "\n\n" + CrashLogPath;
            try
            {
                ThemedDialog.Show(text, global::FastbootEnhance.Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // The themed box is itself WPF; if the failure is in WPF, fall back to the system one.
                MessageBox.Show(text, global::FastbootEnhance.Properties.Resources.error,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Keep the app alive: one failed action should not take the open session down.
            e.Handled = true;
        }

        static void writeCrashLog(string where, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath));
                File.AppendAllText(CrashLogPath,
                    DateTime.Now.ToString("u") + " [" + where + "]\n" + exception + "\n\n");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
