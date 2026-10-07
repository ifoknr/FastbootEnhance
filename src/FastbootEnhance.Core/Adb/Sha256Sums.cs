using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>
    /// A SHA256SUMS file in the format sha256sum writes and checks ("hash  name"), so a
    /// backup can also be checked on Linux or macOS with "sha256sum -c SHA256SUMS".
    /// </summary>
    public static class Sha256Sums
    {
        public const string FileName = "SHA256SUMS";

        public static string Format(IEnumerable<KeyValuePair<string, string>> nameToHash)
        {
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in nameToHash)
                text.Append(entry.Value.ToLowerInvariant()).Append("  ").Append(entry.Key).Append('\n');
            return text.ToString();
        }

        /// <summary>Reads "hash  name" and "hash *name" lines; anything else is ignored.</summary>
        public static List<KeyValuePair<string, string>> Parse(string text)
        {
            List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length < 66 || line[64] != ' ')
                    continue;
                string hash = line.Substring(0, 64);
                if (!IsHex(hash))
                    continue;
                string name = line.Substring(65);
                if (name.StartsWith(" ", StringComparison.Ordinal) || name.StartsWith("*", StringComparison.Ordinal))
                    name = name.Substring(1);
                if (name.Length == 0)
                    continue;
                entries.Add(new KeyValuePair<string, string>(name, hash.ToLowerInvariant()));
            }
            return entries;
        }

        static bool IsHex(string value)
        {
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        public static string HashFile(string path, CancellationToken cancellation = default(CancellationToken))
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        /// <summary>
        /// Checks every file listed in <paramref name="folder"/>'s SHA256SUMS. Names that
        /// would leave the folder are reported as failures rather than read.
        /// </summary>
        public static VerifyReport Verify(string folder, CancellationToken cancellation = default(CancellationToken))
        {
            string sums = Path.Combine(folder, FileName);
            if (!File.Exists(sums))
                return new VerifyReport(false, new List<string>(), new List<string>());

            List<string> good = new List<string>();
            List<string> bad = new List<string>();
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (KeyValuePair<string, string> entry in Parse(File.ReadAllText(sums)))
            {
                string full = Path.GetFullPath(Path.Combine(folder, entry.Key));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add(entry.Key + " (outside the backup folder)");
                    continue;
                }
                if (!File.Exists(full))
                {
                    bad.Add(entry.Key + " (missing)");
                    continue;
                }
                if (HashFile(full, cancellation) == entry.Value)
                    good.Add(entry.Key);
                else
                    bad.Add(entry.Key + " (does not match)");
            }

            return new VerifyReport(true, good, bad);
        }
    }

    public sealed class VerifyReport
    {
        public VerifyReport(bool hadSums, List<string> good, List<string> bad)
        {
            HadSums = hadSums;
            Good = good;
            Bad = bad;
        }

        /// <summary>False when the folder has no SHA256SUMS at all.</summary>
        public bool HadSums { get; }
        public List<string> Good { get; }
        public List<string> Bad { get; }
        public int Total => Good.Count + Bad.Count;
        public bool AllGood => HadSums && Bad.Count == 0 && Good.Count > 0;
    }
}
