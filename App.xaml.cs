using System;
using System.IO;
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
            Path.Combine(Path.GetTempPath(), "FastbootEnhance", "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += onDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                writeCrashLog("background thread", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                writeCrashLog("task", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
        }

        void onDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            writeCrashLog("ui thread", e.Exception);
            MessageBox.Show(
                e.Exception.Message + "\n\n" + CrashLogPath,
                global::FastbootEnhance.Properties.Resources.error, MessageBoxButton.OK, MessageBoxImage.Error);

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
