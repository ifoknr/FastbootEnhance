using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FastbootEnhance
{
    /// <summary>
    /// The user's choices, kept in %APPDATA%\Fastboot Studio\settings.json as a flat object of
    /// strings ("language", "theme"). A missing or damaged file reads as no choices made.
    /// </summary>
    static class Settings
    {
        static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fastboot Studio", "settings.json");

        static Dictionary<string, string> Load()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath))
                    return values;
                using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(FilePath)))
                {
                    if (json.RootElement.ValueKind != JsonValueKind.Object)
                        return values;
                    foreach (JsonProperty property in json.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            values[property.Name] = property.Value.GetString();
                    }
                }
            }
            catch (Exception)
            {
                // A damaged settings file must not stop the app from starting.
            }
            return values;
        }

        public static string Get(string key)
        {
            string value;
            return Load().TryGetValue(key, out value) ? value : null;
        }

        /// <summary>Saves one choice and keeps the others. Returns false when it could not be written.</summary>
        public static bool Set(string key, string value)
        {
            try
            {
                Dictionary<string, string> values = Load();
                values[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(values));
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
