using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace FastbootEnhance
{
    /// <summary>
    /// The interface languages and the one in use. The choice is stored in
    /// %APPDATA%\Fastboot Studio\settings.json; without one the app follows Windows.
    /// It is applied once, before any window exists, so changing it needs a restart.
    /// </summary>
    static class Languages
    {
        public sealed class Option
        {
            public Option(string code, string nativeName)
            {
                Code = code;
                NativeName = nativeName;
            }

            public string Code { get; }

            /// <summary>The language's own name, so a user can find it whatever is showing.</summary>
            public string NativeName { get; }
        }

        public static readonly Option[] All =
        {
            new Option("en", "English"),
            new Option("ar", "العربية"),
            new Option("zh-CN", "简体中文"),
            new Option("ja-JP", "日本語"),
            new Option("ko-KR", "한국어"),
        };

        /// <summary>For automated runs: overrides the saved choice without touching it.</summary>
        const string OverrideVariable = "FASTBOOT_STUDIO_LANG";

        static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fastboot Studio", "settings.json");

        public static CultureInfo Current { get; private set; } = CultureInfo.CurrentUICulture;

        /// <summary>The option matching the language in use, English when none does.</summary>
        public static Option CurrentOption =>
            All.FirstOrDefault(o => string.Equals(o.Code, Current.Name, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(o => o.Code.StartsWith(Current.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            ?? All[0];

        public static bool RightToLeft => Current.TextInfo.IsRightToLeft;

        public static FlowDirection Flow => RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        /// <summary>Picks the language (override, saved choice, then Windows) and applies it.</summary>
        public static void Apply()
        {
            string code = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(code))
                code = LoadSaved();

            CultureInfo culture = CultureInfo.CurrentUICulture;
            if (!string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    culture = CultureInfo.GetCultureInfo(code.Trim());
                }
                catch (CultureNotFoundException)
                {
                }
            }

            Current = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Properties.Resources.Culture = culture;
        }

        static string LoadSaved()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return null;
                using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
                {
                    JsonElement language;
                    return json.RootElement.TryGetProperty("language", out language) && language.ValueKind == JsonValueKind.String
                        ? language.GetString()
                        : null;
                }
            }
            catch (Exception)
            {
                // A damaged settings file must not stop the app from starting.
                return null;
            }
        }

        /// <summary>Saves the choice for the next start. Returns false when it could not be written.</summary>
        public static bool Save(string code)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { language = code }));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
