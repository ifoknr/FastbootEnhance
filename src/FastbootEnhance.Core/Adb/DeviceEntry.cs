using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>A file or folder on the device, as listed by <see cref="AdbCommand.ListFolderCommand"/>.</summary>
    public sealed class DeviceEntry
    {
        public DeviceEntry(string path, string name, bool isFolder, bool isLink, long size)
        {
            Path = path;
            Name = name;
            IsFolder = isFolder;
            IsLink = isLink;
            Size = size;
        }

        public string Path { get; }
        public string Name { get; }
        public bool IsFolder { get; }
        public bool IsLink { get; }
        public long Size { get; }

        /// <summary>
        /// Parses "type|size|path" lines. Folders come first, then files, each by name;
        /// "." and ".." are left out.
        /// </summary>
        public static List<DeviceEntry> ParseListing(string output)
        {
            List<DeviceEntry> entries = new List<DeviceEntry>();
            if (string.IsNullOrEmpty(output))
                return entries;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                // The path is the last field and may itself contain '|'.
                string[] parts = line.Split(new[] { '|' }, 3);
                if (parts.Length < 3 || parts[2].Length == 0)
                    continue;

                string path = parts[2];
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (name.Length == 0 || name == "." || name == "..")
                    continue;

                long size;
                if (!long.TryParse(parts[1], out size))
                    size = -1;

                string type = parts[0];
                bool isFolder = type == "directory";
                bool isLink = type == "symbolic link";
                entries.Add(new DeviceEntry(path, name, isFolder, isLink, isFolder ? -1 : size));
            }

            entries.Sort((x, y) => x.IsFolder != y.IsFolder
                ? (x.IsFolder ? -1 : 1)
                : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        /// <summary>The folder above <paramref name="path"/>; "/" stays "/".</summary>
        public static string Parent(string path)
        {
            string trimmed = (path ?? "/").TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash <= 0 ? "/" : trimmed.Substring(0, slash);
        }

        /// <summary>Joins a device folder and a name with exactly one slash.</summary>
        public static string Combine(string folder, string name)
        {
            return (folder ?? "").TrimEnd('/') + "/" + name;
        }
    }
}
