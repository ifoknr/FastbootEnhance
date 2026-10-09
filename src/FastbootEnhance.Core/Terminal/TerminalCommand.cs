using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using FastbootEnhance.Core.Fastboot;

namespace FastbootEnhance.Core.Terminal
{
    public enum TerminalTool
    {
        Adb,
        Fastboot,
    }

    /// <summary>What a typed command could do that deserves a question first.</summary>
    public enum TerminalRisk
    {
        None,

        /// <summary>Erases user data: -w, erase or format of userdata, metadata or cache, unlocking.</summary>
        WipesData,

        /// <summary>Flashes or erases a boot-chain, radio or identity partition (see PartitionSafety).</summary>
        CriticalPartition,

        /// <summary>Locks the bootloader, which bricks a phone running anything but its stock software.</summary>
        LocksBootloader,

        /// <summary>"adb shell dd ... of=/dev/block/...": writes a partition directly.</summary>
        RawBlockWrite,
    }

    public enum TerminalProblem
    {
        None,
        Empty,

        /// <summary>The Terminal runs adb and fastboot only.</summary>
        NotAdbOrFastboot,

        /// <summary>"adb shell" without a command would wait for typing that never comes.</summary>
        InteractiveShell,

        /// <summary>An opening quote without its closing one.</summary>
        UnclosedQuote,
    }

    /// <summary>
    /// One line typed in the Terminal page: which tool it runs, the arguments as typed, and
    /// whether it needs a question before it runs.
    /// </summary>
    public sealed class TerminalCommand
    {
        TerminalCommand()
        {
        }

        public TerminalTool Tool { get; private set; }

        /// <summary>Everything after the tool's name, exactly as typed (quotes kept).</summary>
        public string Arguments { get; private set; } = "";

        /// <summary>The arguments split like a command line, quotes removed.</summary>
        public IReadOnlyList<string> Words { get; private set; } = new string[0];

        public TerminalProblem Problem { get; private set; }
        public TerminalRisk Risk { get; private set; }

        /// <summary>For <see cref="TerminalRisk.CriticalPartition"/>: the partition named.</summary>
        public string RiskTarget { get; private set; }

        static readonly HashSet<string> FastbootOptionsWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-s", "-S", "-i", "-c", "-b", "-n", "--slot", "--os-version", "--os-patch-level", "--header-version",
            "--kernel-offset", "--ramdisk-offset", "--tags-offset", "--dtb-offset", "--dtb", "--cmdline", "--base", "--page-size",
        };

        static readonly HashSet<string> AdbOptionsWithValue = new HashSet<string>(StringComparer.Ordinal)
        {
            "-s", "-t", "-H", "-P", "-L",
        };

        static readonly HashSet<string> DataPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "userdata", "metadata", "cache",
        };

        static readonly Regex BlockWrite = new Regex(@"\bdd\b.*\bof=\S*/dev/block/", RegexOptions.CultureInvariant);

        public static TerminalCommand Parse(string line)
        {
            TerminalCommand command = new TerminalCommand();
            string text = (line ?? "").Trim();
            if (text.Length == 0)
            {
                command.Problem = TerminalProblem.Empty;
                return command;
            }

            int split = 0;
            while (split < text.Length && !char.IsWhiteSpace(text[split]))
                split++;
            string tool = text.Substring(0, split).ToLowerInvariant();
            if (tool.EndsWith(".exe", StringComparison.Ordinal))
                tool = tool.Substring(0, tool.Length - 4);

            if (tool == "adb")
                command.Tool = TerminalTool.Adb;
            else if (tool == "fastboot")
                command.Tool = TerminalTool.Fastboot;
            else
            {
                command.Problem = TerminalProblem.NotAdbOrFastboot;
                return command;
            }

            command.Arguments = text.Substring(split).Trim();
            List<string> words;
            if (!TrySplit(command.Arguments, out words))
            {
                command.Problem = TerminalProblem.UnclosedQuote;
                return command;
            }
            command.Words = words;

            if (command.Tool == TerminalTool.Fastboot)
                command.ClassifyFastboot(words);
            else
                command.ClassifyAdb(words);
            return command;
        }

        void ClassifyFastboot(List<string> words)
        {
            int verb = SkipOptions(words, FastbootOptionsWithValue);
            if (words.Contains("-w"))
                Raise(TerminalRisk.WipesData);
            if (verb >= words.Count)
                return;

            string action = words[verb].ToLowerInvariant();
            string next = verb + 1 < words.Count ? words[verb + 1] : null;

            // "format:ext4 userdata" names the file system with a colon.
            int colon = action.IndexOf(':');
            if (colon > 0)
                action = action.Substring(0, colon);

            switch (action)
            {
                case "flash":
                case "erase":
                case "format":
                    if (next == null)
                        break;
                    if (PartitionSafety.IsBrickRisk(next))
                    {
                        Raise(TerminalRisk.CriticalPartition);
                        RiskTarget = next;
                    }
                    else if (action != "flash" && DataPartitions.Contains(PartitionSafety.BaseName(next)))
                    {
                        Raise(TerminalRisk.WipesData);
                    }
                    break;

                case "flashing":
                case "oem":
                    if (next == null)
                        break;
                    string what = next.ToLowerInvariant();
                    if (what == "lock" || what == "lock_critical")
                        Raise(TerminalRisk.LocksBootloader);
                    else if (what == "unlock" || what == "unlock_critical")
                        Raise(TerminalRisk.WipesData);
                    break;
            }
        }

        void ClassifyAdb(List<string> words)
        {
            int verb = SkipOptions(words, AdbOptionsWithValue);
            if (verb >= words.Count || words[verb] != "shell")
                return;

            // Shell options ("-t", "-x"...) come before the command.
            int first = verb + 1;
            while (first < words.Count && words[first].StartsWith("-", StringComparison.Ordinal))
                first++;
            if (first >= words.Count)
            {
                Problem = TerminalProblem.InteractiveShell;
                return;
            }

            string script = string.Join(" ", words.GetRange(first, words.Count - first));
            if (BlockWrite.IsMatch(script))
                Raise(TerminalRisk.RawBlockWrite);
        }

        /// <summary>The index of the first word that is not an option or an option's value.</summary>
        static int SkipOptions(List<string> words, HashSet<string> withValue)
        {
            int i = 0;
            while (i < words.Count && words[i].StartsWith("-", StringComparison.Ordinal))
            {
                i += withValue.Contains(words[i]) ? 2 : 1;
            }
            return i;
        }

        // The most serious risk wins: locking, then critical partitions, raw writes, wiping data.
        void Raise(TerminalRisk risk)
        {
            if (Rank(risk) > Rank(Risk))
                Risk = risk;
        }

        static int Rank(TerminalRisk risk)
        {
            switch (risk)
            {
                case TerminalRisk.LocksBootloader: return 4;
                case TerminalRisk.CriticalPartition: return 3;
                case TerminalRisk.RawBlockWrite: return 2;
                case TerminalRisk.WipesData: return 1;
                default: return 0;
            }
        }

        /// <summary>Splits like a command line: spaces separate words, double or single quotes group them.</summary>
        public static bool TrySplit(string text, out List<string> words)
        {
            words = new List<string>();
            StringBuilder word = new StringBuilder();
            bool inWord = false;
            char quote = '\0';

            foreach (char c in text)
            {
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    else
                        word.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    inWord = true;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    if (inWord)
                    {
                        words.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }
                    continue;
                }
                word.Append(c);
                inWord = true;
            }

            if (quote != '\0')
                return false;
            if (inWord)
                words.Add(word.ToString());
            return true;
        }
    }
}
