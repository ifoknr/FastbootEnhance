using System;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// Interaction logic for Logger.xaml
    /// </summary>
    public partial class Logger : Window
    {
        public Logger()
        {
            InitializeComponent();
        }

        public Logger(Action onClose)
        {
            InitializeComponent();
            Closed += (a, b) => onClose();
        }

        public void appendLog(string logs)
        {
            Dispatcher.Invoke(new Action(delegate
            {
                log.Text += logs + "\n";
                scroller.ScrollToEnd();
            }));
        }
    }
}
