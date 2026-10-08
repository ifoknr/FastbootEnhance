using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastbootEnhance
{
    /// <summary>
    /// Wires a window that uses the ThemedWindow style: its caption buttons, and the inset a
    /// maximised window needs. Windows hangs a maximised window's frame off the edges of the
    /// screen, so without the inset the title bar and the outer pixels of the content are cut off.
    /// </summary>
    static class ThemedWindow
    {
        const int SM_CXSIZEFRAME = 32;
        const int SM_CYSIZEFRAME = 33;
        const int SM_CXPADDEDBORDER = 92;

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        public static void Attach(Window window)
        {
            window.Style = (Style)window.FindResource("ThemedWindow");

            window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
                delegate { SystemCommands.MinimizeWindow(window); }));
            window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
                delegate { SystemCommands.MaximizeWindow(window); }));
            window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
                delegate { SystemCommands.RestoreWindow(window); }));
            window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
                delegate { SystemCommands.CloseWindow(window); }));

            window.SourceInitialized += delegate { DarkTitleBar.Apply(window); };
            window.StateChanged += delegate { fitFrame(window); };
            window.Loaded += delegate { fitFrame(window); };
        }

        static void fitFrame(Window window)
        {
            Border frame = window.Template == null ? null : window.Template.FindName("frame", window) as Border;
            if (frame == null)
                return;

            if (window.WindowState != WindowState.Maximized)
            {
                frame.Margin = new Thickness(0);
                return;
            }

            // The overhang, in device pixels, turned into the window's own units for this DPI.
            int x = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            int y = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
            PresentationSource source = PresentationSource.FromVisual(window);
            double scaleX = 1, scaleY = 1;
            if (source != null && source.CompositionTarget != null)
            {
                scaleX = source.CompositionTarget.TransformFromDevice.M11;
                scaleY = source.CompositionTarget.TransformFromDevice.M22;
            }
            frame.Margin = new Thickness(x * scaleX, y * scaleY, x * scaleX, y * scaleY);
        }
    }
}
