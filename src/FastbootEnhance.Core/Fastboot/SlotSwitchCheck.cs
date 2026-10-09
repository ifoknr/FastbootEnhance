using System;
using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance.Core.Fastboot
{
    public enum SlotCheckLevel
    {
        /// <summary>Nothing against switching.</summary>
        Ok,

        /// <summary>Worth knowing, normal in some cases (a slot just flashed has not booted yet).</summary>
        Warn,

        /// <summary>The slot is likely not to boot; switch only on purpose.</summary>
        Danger,

        /// <summary>The slot cannot boot as it is; switching is refused.</summary>
        Block,
    }

    public enum SlotCheckReason
    {
        /// <summary>The bootloader reports nothing about the slot's state.</summary>
        StateUnknown,

        /// <summary>slot-successful is no: the slot never completed a boot.</summary>
        NeverBooted,

        /// <summary>slot-unbootable is yes.</summary>
        Unbootable,

        /// <summary>slot-retry-count is 0 on a slot that has not booted.</summary>
        NoRetriesLeft,

        /// <summary>In fastbootd: logical partitions of the target slot are missing or empty.</summary>
        EmptyPartitions,

        /// <summary>In the bootloader logical partitions are not listed, so they could not be checked.</summary>
        PartitionsNotChecked,
    }

    public sealed class SlotCheckFinding
    {
        internal SlotCheckFinding(SlotCheckLevel level, SlotCheckReason reason, IReadOnlyList<string> partitions = null)
        {
            Level = level;
            Reason = reason;
            Partitions = partitions ?? new string[0];
        }

        public SlotCheckLevel Level { get; }
        public SlotCheckReason Reason { get; }

        /// <summary>For <see cref="SlotCheckReason.EmptyPartitions"/>: the partitions concerned.</summary>
        public IReadOnlyList<string> Partitions { get; }
    }

    /// <summary>
    /// What is known about a slot before switching to it, from "getvar all": its boot state
    /// (slot-successful, slot-unbootable, slot-retry-count) and, in fastbootd, whether the
    /// logical partitions it needs exist with data.
    /// </summary>
    public sealed class SlotSwitchCheck
    {
        SlotSwitchCheck(string target, IReadOnlyList<SlotCheckFinding> findings)
        {
            Target = target;
            Findings = findings;
        }

        public string Target { get; }
        public IReadOnlyList<SlotCheckFinding> Findings { get; }

        public SlotCheckLevel Level => Findings.Count == 0 ? SlotCheckLevel.Ok : Findings.Max(f => f.Level);

        public static SlotSwitchCheck Evaluate(FastbootData data, string target)
        {
            List<SlotCheckFinding> findings = new List<SlotCheckFinding>();

            bool successful;
            bool hasSuccessful = data.slot_successful.TryGetValue(target, out successful);
            bool unbootable;
            bool hasUnbootable = data.slot_unbootable.TryGetValue(target, out unbootable);
            int retries;
            bool hasRetries = data.slot_retry_count.TryGetValue(target, out retries);

            if (!hasSuccessful && !hasUnbootable && !hasRetries)
                findings.Add(new SlotCheckFinding(SlotCheckLevel.Ok, SlotCheckReason.StateUnknown));
            if (hasUnbootable && unbootable)
                findings.Add(new SlotCheckFinding(SlotCheckLevel.Danger, SlotCheckReason.Unbootable));
            if (hasRetries && retries == 0 && !(hasSuccessful && successful))
                findings.Add(new SlotCheckFinding(SlotCheckLevel.Danger, SlotCheckReason.NoRetriesLeft));
            else if (hasSuccessful && !successful)
                findings.Add(new SlotCheckFinding(SlotCheckLevel.Warn, SlotCheckReason.NeverBooted));

            // Logical partitions are only listed by fastbootd. A partition the current slot has
            // with data but the target slot lacks (or has empty) means the target cannot mount it.
            if (data.fastbootd && data.current_slot != null)
            {
                string current = "_" + data.current_slot;
                string other = "_" + target;
                List<string> missing = new List<string>();
                foreach (KeyValuePair<string, bool?> entry in data.partition_is_logical)
                {
                    if (entry.Value != true || !entry.Key.EndsWith(current, StringComparison.Ordinal))
                        continue;
                    long size;
                    if (!data.partition_size.TryGetValue(entry.Key, out size) || size <= 0)
                        continue;
                    string partner = entry.Key.Substring(0, entry.Key.Length - current.Length) + other;
                    long partnerSize;
                    if (!data.partition_size.TryGetValue(partner, out partnerSize) || partnerSize <= 0)
                        missing.Add(partner);
                }
                if (missing.Count > 0)
                {
                    missing.Sort(StringComparer.Ordinal);
                    findings.Add(new SlotCheckFinding(SlotCheckLevel.Block, SlotCheckReason.EmptyPartitions, missing));
                }
            }
            else
            {
                findings.Add(new SlotCheckFinding(SlotCheckLevel.Ok, SlotCheckReason.PartitionsNotChecked));
            }

            return new SlotSwitchCheck(target, findings);
        }
    }
}
