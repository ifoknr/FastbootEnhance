using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FastbootEnhance
{
    /// <summary>
    /// Light or dark. Themes/Dark.xaml holds the dark palette; for light, its colour entries are
    /// replaced before any window exists, so every style and page picks the light ones up.
    /// Like the language, the choice is saved in settings.json and takes effect on restart.
    /// </summary>
    static class Theme
    {
        public enum Mode
        {
            Dark,
            Light,

            /// <summary>Follows Windows' "app mode" (Settings → Personalisation → Colours).</summary>
            System,
        }

        /// <summary>For automated runs: overrides the saved choice without touching it.</summary>
        const string OverrideVariable = "FASTBOOT_STUDIO_THEME";

        public static Mode Chosen { get; private set; } = Mode.Dark;

        public static bool IsLight { get; private set; }

        // The same roles as the dark palette in Themes/Dark.xaml. The accent, amber and red are
        // darker here so they keep their contrast on white.
        static readonly Dictionary<string, string> LightPalette = new Dictionary<string, string>
        {
            { "Bg", "#F3F5F8" },
            { "Rail", "#E9EDF2" },
            { "Well", "#F7F9FB" },
            { "Surface", "#FFFFFF" },
            { "Raised", "#EEF1F5" },
            { "Hover", "#E3E8EE" },
            { "Line", "#D3DAE3" },
            { "Text", "#141A22" },
            { "Dim", "#566273" },
            { "Faint", "#768293" },
            { "Accent", "#0B8F80" },
            { "AccentInk", "#FFFFFF" },
            { "AccentSoft", "#E2F4F1" },
            { "AccentLine", "#97D3CA" },
            { "Warn", "#A86A12" },
            { "WarnSoft", "#FFF5E3" },
            { "WarnLine", "#EBCB8F" },
            { "Danger", "#C9332E" },
            { "DangerSoft", "#FDEDEC" },
            { "DangerLine", "#F0B6B3" },
            { "Ok", "#23904A" },
            { "Thumb", "#BFC8D3" },
        };

        public static Mode Parse(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "light": return Mode.Light;
                case "system": return Mode.System;
                default: return Mode.Dark;
            }
        }

        /// <summary>Picks the theme (override, saved choice, dark) and applies it to the app's resources.</summary>
        public static void Apply(ResourceDictionary resources)
        {
            string raw = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(raw))
                raw = Settings.Get("theme");
            Chosen = Parse(raw);
            IsLight = Chosen == Mode.Light || (Chosen == Mode.System && WindowsUsesLightApps());
            if (!IsLight)
                return;

            ResourceDictionary palette = resources.MergedDictionaries[0];
            foreach (KeyValuePair<string, string> entry in LightPalette)
                palette[entry.Key] = Brush(entry.Value);

            // Scroll bar corners and other bits WPF draws in system colours.
            SolidColorBrush surface = Brush(LightPalette["Surface"]);
            palette[SystemColors.ControlBrushKey] = surface;
            palette[SystemColors.ControlLightBrushKey] = surface;
            palette[SystemColors.ControlLightLightBrushKey] = surface;
            palette[SystemColors.ControlDarkBrushKey] = surface;
        }

        public static bool Save(Mode mode)
        {
            return Settings.Set("theme", mode.ToString().ToLowerInvariant());
        }

        static SolidColorBrush Brush(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        static bool WindowsUsesLightApps()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key?.GetValue("AppsUseLightTheme");
                    return value is int light && light != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
