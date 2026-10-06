using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace FastbootEnhance
{
    class Helper
    {

        public class TaskbarItemHelper
        {
            public static void start()
            {
                MainWindow.THIS.taskbariteminfo.ProgressValue = 0;
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            }

            public static void stop()
            {
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            }

            public static void update(int percent)
            {
                MainWindow.THIS.taskbariteminfo.ProgressValue = percent / 100.0;
            }

            public static void error()
            {
                MainWindow.THIS.taskbariteminfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Error;
            }
        }

        public static void offloadAndRun(Action bigtask, Action callbackOnUIThread)
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                bigtask();
                MainWindow.THIS.Dispatcher.Invoke(callbackOnUIThread);
            }));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>A yes/no prompt. Returns true only when the user picks yes.</summary>
        public static bool confirm(string message, string title)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;
        }

        public class ListHelper<T>
        {
            public delegate bool Filter(T t);

            ListView listView;
            List<T> items;
            Filter filter;

            public ListHelper(ListView listView, Filter filter)
            {
                this.listView = listView;
                items = new List<T>();
                this.filter = filter;
            }

            public void clear()
            {
                listView.SelectedItems.Clear();
                listView.Items.Clear();
                items = new List<T>();
            }

            public void addItem(T item)
            {
                items.Add(item);
            }

            public void render()
            {
                listView.Items.Clear();
                foreach (T t in items)
                {
                    listView.Items.Add(t);
                }
            }

            public void doFilter()
            {
                listView.Items.Clear();
                foreach (T t in items)
                {
                    if (filter(t))
                        listView.Items.Add(t);
                }
            }
        }

        public delegate void PathSelectCallback(string path);

        public static void fileSelect(PathSelectCallback callback, string filter = "All Files|*.*")
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Filter = filter;
            if (dialog.ShowDialog() == true)
            {
                callback(dialog.FileName);
            }
        }

        public static void pathSelect(PathSelectCallback callback)
        {
            Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog();
            dialog.Title = Properties.Resources.select_save_path;
            if (dialog.ShowDialog() == true)
            {
                callback(dialog.FolderName);
            }
        }

        /// <summary>
        /// Formats a unix timestamp in the machine's own time zone. Timestamps come from the
        /// payload manifest, so a nonsensical value must not be allowed to throw.
        /// </summary>
        public static DateTime? timeStamp2DataTime(Int64 timestamp)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public static string byte2AUnit(ulong size)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = size;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return (unit == 0 ? value.ToString("F0") : value.ToString("F2")) + " " + units[unit];
        }

        public static string byte2AUnit(long size)
        {
            return size < 0 ? "-" : byte2AUnit((ulong)size);
        }
    }
}
