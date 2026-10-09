using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// The app's own message box, drawn in the dark theme. It takes the same arguments as
    /// System.Windows.MessageBox and returns the same results, so call sites read the same.
    /// </summary>
    public partial class ThemedDialog : Window
    {
        enum Kind
        {
            Info,
            Done,
            Question,
            Warning,
            Error
        }

        // Drawn as strokes, like the navigation icons.
        const string GlyphInfo = "M12,2 A10,10 0 1 1 11.99,2 Z M12,11 V17 M12,7 V7.5";
        const string GlyphDone = "M5,12.5 L10,17.5 L19.5,7";
        const string GlyphQuestion = "M12,2 A10,10 0 1 1 11.99,2 Z M9.3,9.2 A2.8,2.8 0 1 1 12,12 V13.8 M12,17 V17.5";
        const string GlyphWarning = "M12,3 L22,20.5 H2 Z M12,9.5 V14 M12,17 V17.5";
        const string GlyphError = "M12,2 A10,10 0 1 1 11.99,2 Z M9,9 L15,15 M15,9 L9,15";

        MessageBoxResult result;

        ThemedDialog(string text, string title, MessageBoxButton choice, Kind kind, MessageBoxResult defaultResult,
            string requiredWord = null)
        {
            InitializeComponent();
            FlowDirection = Languages.Flow;

            heading.Text = string.IsNullOrEmpty(title) ? defaultHeading(kind) : title;
            message.Text = text ?? "";
            Title = heading.Text;
            if (message.Text.Length == 0)
                message.Visibility = Visibility.Collapsed;

            string color = kind == Kind.Error ? "Danger" : kind == Kind.Warning ? "Warn" : kind == Kind.Done ? "Ok" : "Accent";
            string soft = kind == Kind.Error ? "DangerSoft" : kind == Kind.Warning ? "WarnSoft" : "AccentSoft";
            string line = kind == Kind.Error ? "DangerLine" : kind == Kind.Warning ? "WarnLine" : "AccentLine";
            strip.Background = Palette.Get(color);
            badge.Background = Palette.Get(soft);
            badge.BorderBrush = Palette.Get(line);
            glyph.Stroke = Palette.Get(color);
            glyph.Data = Geometry.Parse(kind == Kind.Error ? GlyphError
                : kind == Kind.Warning ? GlyphWarning
                : kind == Kind.Question ? GlyphQuestion
                : kind == Kind.Done ? GlyphDone
                : GlyphInfo);

            // A "yes" that answers a warning or an error is the risky choice, so it is drawn red.
            bool risky = kind == Kind.Warning || kind == Kind.Error;
            switch (choice)
            {
                case MessageBoxButton.YesNo:
                    addButtons(defaultResult, MessageBoxResult.No, MessageBoxResult.No, risky,
                        MessageBoxResult.No, MessageBoxResult.Yes);
                    break;
                case MessageBoxButton.YesNoCancel:
                    addButtons(defaultResult, MessageBoxResult.Yes, MessageBoxResult.Cancel, risky,
                        MessageBoxResult.Cancel, MessageBoxResult.No, MessageBoxResult.Yes);
                    break;
                case MessageBoxButton.OKCancel:
                    addButtons(defaultResult, MessageBoxResult.OK, MessageBoxResult.Cancel, risky,
                        MessageBoxResult.Cancel, MessageBoxResult.OK);
                    break;
                default:
                    addButtons(defaultResult, MessageBoxResult.OK, MessageBoxResult.OK, false,
                        MessageBoxResult.OK);
                    break;
            }

            // Closing with Escape or Alt+F4 counts as the safe answer, as with MessageBox.
            result = choice == MessageBoxButton.OK ? MessageBoxResult.OK
                : choice == MessageBoxButton.YesNo ? MessageBoxResult.No
                : MessageBoxResult.Cancel;

            if (requiredWord != null)
                RequireTyped(requiredWord);

            header.MouseLeftButtonDown += delegate { DragMove(); };
            PreviewKeyDown += onKey;
            SourceInitialized += delegate { DarkTitleBar.Apply(this); };
        }

        /// <summary>
        /// Keeps the main (last) button disabled until <paramref name="word"/> is typed, and makes
        /// it the only way to say yes: Enter does nothing until then.
        /// </summary>
        void RequireTyped(string word)
        {
            typed_panel.Visibility = Visibility.Visible;
            typed_prompt.Text = string.Format(Properties.Resources.confirm_type_name, word);
            Button main = buttons.Children.OfType<Button>().Last();
            main.IsEnabled = false;
            main.IsDefault = false;
            typed.TextChanged += delegate
            {
                main.IsEnabled = string.Equals(typed.Text.Trim(), word, StringComparison.OrdinalIgnoreCase);
            };
            Loaded += delegate { typed.Focus(); };
        }

        /// <summary>
        /// Buttons left to right; the last one is the main action. When the caller names no
        /// default, Enter goes to <paramref name="fallbackDefault"/>.
        /// </summary>
        void addButtons(MessageBoxResult requestedDefault, MessageBoxResult fallbackDefault,
            MessageBoxResult cancelResult, bool risky, params MessageBoxResult[] order)
        {
            MessageBoxResult chosenDefault = order.Contains(requestedDefault) ? requestedDefault : fallbackDefault;

            for (int i = 0; i < order.Length; i++)
            {
                MessageBoxResult value = order[i];
                bool main = i == order.Length - 1;

                Button button = new Button
                {
                    Content = label(value),
                    MinWidth = 96,
                    Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                    IsDefault = value == chosenDefault,
                    IsCancel = value == cancelResult,
                };
                if (main)
                    button.Style = (Style)FindResource(risky ? "DangerButton" : "AccentButton");

                button.Click += delegate
                {
                    result = value;
                    Close();
                };
                buttons.Children.Add(button);
            }
        }

        void onKey(object sender, KeyEventArgs e)
        {
            // Ctrl+C copies the message, as a Windows message box does; handy for error reports.
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                try
                {
                    Clipboard.SetText(heading.Text + "\n\n" + message.Text);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                }
                e.Handled = true;
            }
        }

        static string label(MessageBoxResult value)
        {
            switch (value)
            {
                case MessageBoxResult.Yes: return Properties.Resources.yes;
                case MessageBoxResult.No: return Properties.Resources.no;
                case MessageBoxResult.Cancel: return Properties.Resources.cancel;
                default: return Properties.Resources.ok;
            }
        }

        static string defaultHeading(Kind kind)
        {
            switch (kind)
            {
                case Kind.Error: return Properties.Resources.error;
                case Kind.Warning: return Properties.Resources.dialog_warning;
                case Kind.Question: return Properties.Resources.confirm_title;
                case Kind.Done: return Properties.Resources.dialog_done;
                default: return Properties.Resources.app_name;
            }
        }

        static Kind kindOf(MessageBoxImage image)
        {
            // MessageBoxImage aliases share values: Hand = Stop = Error, Exclamation = Warning.
            switch (image)
            {
                case MessageBoxImage.Error: return Kind.Error;
                case MessageBoxImage.Warning: return Kind.Warning;
                case MessageBoxImage.Question: return Kind.Question;
                default: return Kind.Info;
            }
        }

        public static MessageBoxResult Show(string text)
        {
            return show(text, null, MessageBoxButton.OK, Kind.Info, MessageBoxResult.None);
        }

        public static MessageBoxResult Show(string text, string title)
        {
            return show(text, title, MessageBoxButton.OK, Kind.Info, MessageBoxResult.None);
        }

        public static MessageBoxResult Show(string text, string title, MessageBoxButton choice, MessageBoxImage image)
        {
            return show(text, title, choice, kindOf(image), MessageBoxResult.None);
        }

        public static MessageBoxResult Show(string text, string title, MessageBoxButton choice, MessageBoxImage image,
            MessageBoxResult defaultResult)
        {
            return show(text, title, choice, kindOf(image), defaultResult);
        }

        /// <summary>
        /// A warning whose Yes only becomes available once <paramref name="word"/> (a partition
        /// name) has been typed, for actions that can leave a phone unable to start.
        /// </summary>
        public static bool ConfirmTyped(string text, string title, string word)
        {
            return show(text, title, MessageBoxButton.YesNo, Kind.Error, MessageBoxResult.No, word) == MessageBoxResult.Yes;
        }

        /// <summary>A finished operation: the same box, in green with a tick.</summary>
        public static void Done(string text)
        {
            show(text, null, MessageBoxButton.OK, Kind.Done, MessageBoxResult.None);
        }

        static MessageBoxResult show(string text, string title, MessageBoxButton choice, Kind kind,
            MessageBoxResult defaultResult, string requiredWord = null)
        {
            Application app = Application.Current;
            if (app == null)
                return MessageBox.Show(text, title ?? "", choice);

            // Like MessageBox, callable from any thread; the window itself lives on the UI thread.
            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => show(text, title, choice, kind, defaultResult, requiredWord));

            ThemedDialog dialog = new ThemedDialog(text, title, choice, kind, defaultResult, requiredWord);
            Window owner = app.Windows.OfType<Window>()
                .FirstOrDefault(window => window != dialog && window.IsActive && window.IsVisible)
                ?? (MainWindow.THIS != null && MainWindow.THIS.IsVisible ? MainWindow.THIS : null);
            if (owner != null)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
            }

            dialog.ShowDialog();
            return dialog.result;
        }
    }
}
