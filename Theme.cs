using Microsoft.Win32;
using System;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// Light or dark. The colours live in Themes/Palette.Dark.xaml and Palette.Light.xaml (the
    /// same keys); one of them is loaded before Themes/Styles.xaml, before any window exists,
    /// so every style and page takes its colours from it. Like the language, the choice is
    /// saved in settings.json and takes effect on restart.
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

        public static Mode Parse(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "light": return Mode.Light;
                case "system": return Mode.System;
                default: return Mode.Dark;
            }
        }

        /// <summary>
        /// Picks the theme (override, saved choice, dark) and loads its palette and then the
        /// styles into the app's resources. Called once, before the first window.
        /// </summary>
        public static void Apply(ResourceDictionary resources)
        {
            string raw = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(raw))
                raw = Settings.Get("theme");
            Chosen = Parse(raw);
            IsLight = Chosen == Mode.Light || (Chosen == Mode.System && WindowsUsesLightApps());

            resources.MergedDictionaries.Add(Load(IsLight ? "Themes/Palette.Light.xaml" : "Themes/Palette.Dark.xaml"));
            resources.MergedDictionaries.Add(Load("Themes/Styles.xaml"));
        }

        static ResourceDictionary Load(string path)
        {
            return new ResourceDictionary { Source = new Uri("pack://application:,,,/" + path, UriKind.Absolute) };
        }

        public static bool Save(Mode mode)
        {
            return Settings.Set("theme", mode.ToString().ToLowerInvariant());
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
