using System;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// Interaction logic for FastbootActionWindow.xaml
    /// </summary>
    public partial class FastbootActionWindow : Window
    {
        public enum StartType
        {
            CREATE,
            RESIZE
        }

        public delegate void FastbootLogicalCallback(string name, ulong size);

        public FastbootActionWindow(StartType type,
            string partition_name, long size, FastbootLogicalCallback callback)
        {
            InitializeComponent();
            Owner = MainWindow.THIS;
            FlowDirection = Languages.Flow;
            ThemedWindow.Attach(this);

            this.name.Text = partition_name;
            this.size.Text = size.ToString();

            this.ok.Click += delegate
            {
                if (this.name.Text == "")
                {
                    ThemedDialog.Show(Properties.Resources.fastboot_partition_name_empty);
                    return;
                }

                if (this.size.Text == "")
                {
                    ThemedDialog.Show(Properties.Resources.fastboot_partition_size_empty);
                    return;
                }

                ulong new_size = 0;
                try
                {
                    new_size = Convert.ToUInt64(this.size.Text);
                }
                catch (Exception)
                {
                    ThemedDialog.Show(Properties.Resources.fastboot_partition_size_invalid);
                    return;
                }

                if (type == StartType.RESIZE && new_size == (ulong)size)
                {
                    Close();
                    return;
                }

                if (type == StartType.RESIZE && new_size < (ulong)size)
                {
                    ThemedDialog.Show(Properties.Resources.fastboot_partition_size_unable_shrink);
                    return;
                }

                callback(this.name.Text, new_size);
                Close();
            };

            switch (type)
            {
                case StartType.CREATE:
                    this.Title = Properties.Resources.fastboot_create_dynamic_partition;
                    // fastbootd adds new partitions to the "default" group, not the phone's own.
                    this.note.Text = Properties.Resources.create_default_group_note;
                    this.note.Visibility = Visibility.Visible;
                    break;
                case StartType.RESIZE:
                    this.Title = Properties.Resources.fastboot_expand_dynamic_partition;
                    this.name.IsReadOnly = true;
                    break;
            }
        }
    }
}
