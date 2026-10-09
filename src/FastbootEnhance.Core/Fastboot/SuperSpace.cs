using System;
using System.Collections.Generic;
using System.Linq;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>A logical partition and the bytes it takes in super.</summary>
    public sealed class LogicalPartitionSize
    {
        internal LogicalPartitionSize(string name, long size)
        {
            Name = name;
            Size = size;
        }

        public string Name { get; }
        public long Size { get; }
    }

    /// <summary>
    /// How the super partition is shared out, from what fastbootd reports in "getvar all":
    /// the size of super and the size of every logical partition. The numbers are estimates:
    /// fastboot does not report the metadata area, the alignment or the group limits, so a
    /// little room is kept back for them.
    /// </summary>
    public sealed class SuperSpace
    {
        /// <summary>liblp places partitions on 1 MiB boundaries.</summary>
        public const long Alignment = 1L << 20;

        /// <summary>Geometry, metadata copies and the first alignment gap, rounded up.</summary>
        public const long MetadataReserve = 4L << 20;

        SuperSpace()
        {
        }

        public string SuperName { get; private set; }

        /// <summary>Bytes in super; -1 when the phone does not say (the bootloader, older phones).</summary>
        public long SuperSize { get; private set; } = -1;

        public bool Known => SuperSize > 0;

        public string CurrentSlot { get; private set; }
        public string OtherSlot { get; private set; }

        public long CurrentSlotBytes { get; private set; }
        public long OtherSlotBytes { get; private set; }

        /// <summary>Copy-on-write partitions left by a Virtual A/B update ("system_b-cow").</summary>
        public long CowBytes { get; private set; }

        /// <summary>Logical partitions without a slot suffix.</summary>
        public long UnslottedBytes { get; private set; }

        /// <summary>Partitions of the other slot that hold data, largest first.</summary>
        public IReadOnlyList<LogicalPartitionSize> OtherSlotPartitions { get; private set; } = new LogicalPartitionSize[0];

        public IReadOnlyList<LogicalPartitionSize> CowPartitions { get; private set; } = new LogicalPartitionSize[0];

        public long Used => CurrentSlotBytes + OtherSlotBytes + CowBytes + UnslottedBytes;

        /// <summary>What is left for growing or adding partitions.</summary>
        public long Free => Known ? Math.Max(0, SuperSize - MetadataReserve - Used) : -1;

        // Logical partition (by full name) to its size in super, aligned.
        readonly Dictionary<string, long> logical = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>The bytes an image takes once written: the expanded size of a sparse image.</summary>
        public static long WrittenSize(string path)
        {
            if (Images.SparseImage.IsSparse(path))
                return Images.SparseImage.Open(path).OutputLength;
            return new System.IO.FileInfo(path).Length;
        }

        public static long Align(long size)
        {
            if (size <= 0)
                return 0;
            return (size + Alignment - 1) / Alignment * Alignment;
        }

        public static SuperSpace From(FastbootData data)
        {
            SuperSpace space = new SuperSpace();
            space.CurrentSlot = data.current_slot;
            space.OtherSlot = data.current_slot == "a" ? "b" : data.current_slot == "b" ? "a" : null;
            space.SuperName = string.IsNullOrEmpty(data.super_partition_name) ? "super" : data.super_partition_name;

            long superSize;
            if (data.partition_size.TryGetValue(space.SuperName, out superSize) && superSize > 0)
                space.SuperSize = superSize;

            List<LogicalPartitionSize> other = new List<LogicalPartitionSize>();
            List<LogicalPartitionSize> cow = new List<LogicalPartitionSize>();

            foreach (KeyValuePair<string, bool?> entry in data.partition_is_logical)
            {
                if (entry.Value != true)
                    continue;
                long size;
                if (!data.partition_size.TryGetValue(entry.Key, out size) || size < 0)
                    size = 0;
                long aligned = Align(size);
                string name = entry.Key;
                space.logical[name] = aligned;

                if (name.EndsWith("-cow", StringComparison.Ordinal))
                {
                    space.CowBytes += aligned;
                    if (aligned > 0)
                        cow.Add(new LogicalPartitionSize(name, aligned));
                }
                else if (space.CurrentSlot != null && name.EndsWith("_" + space.CurrentSlot, StringComparison.Ordinal))
                {
                    space.CurrentSlotBytes += aligned;
                }
                else if (space.OtherSlot != null && name.EndsWith("_" + space.OtherSlot, StringComparison.Ordinal))
                {
                    space.OtherSlotBytes += aligned;
                    if (aligned > 0)
                        other.Add(new LogicalPartitionSize(name, aligned));
                }
                else
                {
                    space.UnslottedBytes += aligned;
                }
            }

            space.OtherSlotPartitions = other.OrderByDescending(p => p.Size).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
            space.CowPartitions = cow.OrderByDescending(p => p.Size).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
            return space;
        }

        /// <summary>
        /// The logical partition a write to <paramref name="name"/> lands on: the name itself
        /// when the phone has it, otherwise the name with the current slot ("system" is
        /// "system_a"). Null when it is not a logical partition the phone has.
        /// </summary>
        public string ResolveLogical(string name)
        {
            if (logical.ContainsKey(name))
                return name;
            if (CurrentSlot != null && logical.ContainsKey(name + "_" + CurrentSlot))
                return name + "_" + CurrentSlot;
            return null;
        }

        /// <summary>
        /// Whether writing these images one after the other fits in super. fastbootd resizes a
        /// logical partition to the size of the image written to it, so each write grows or
        /// shrinks its partition; the space needed is the highest point reached on the way.
        /// <paramref name="writes"/> maps a partition name (with or without the slot) to the
        /// image size; names that are not logical partitions are ignored, unless listed in
        /// <paramref name="created"/>, the partitions that will be created before writing.
        /// </summary>
        public SuperFit Check(IEnumerable<KeyValuePair<string, long>> writes, IEnumerable<string> created = null)
        {
            HashSet<string> creating = new HashSet<string>(created ?? new string[0], StringComparer.Ordinal);
            Dictionary<string, long> sizes = new Dictionary<string, long>(logical, StringComparer.Ordinal);
            long change = 0;
            long peak = 0;
            List<string> growing = new List<string>();

            foreach (KeyValuePair<string, long> write in writes)
            {
                string target = ResolveLogical(write.Key);
                if (target == null)
                {
                    if (!creating.Contains(write.Key))
                        continue;
                    target = write.Key;
                }

                long before;
                sizes.TryGetValue(target, out before);
                long after = Align(write.Value);
                sizes[target] = after;
                change += after - before;
                if (after > before)
                    growing.Add(target);
                peak = Math.Max(peak, change);
            }

            return new SuperFit(peak, Free, growing);
        }
    }

    /// <summary>The answer of <see cref="SuperSpace.Check"/>.</summary>
    public sealed class SuperFit
    {
        internal SuperFit(long needed, long free, IReadOnlyList<string> growing)
        {
            Needed = needed;
            Free = free;
            Growing = growing;
        }

        /// <summary>Extra bytes super must have at the fullest point of the writes.</summary>
        public long Needed { get; }

        /// <summary>Free bytes now; -1 when the size of super is unknown.</summary>
        public long Free { get; }

        /// <summary>Partitions that get bigger.</summary>
        public IReadOnlyList<string> Growing { get; }

        /// <summary>False only when it is known not to fit.</summary>
        public bool Fits => Free < 0 || Needed <= Free;

        public long Shortfall => Fits ? 0 : Needed - Free;
    }
}
