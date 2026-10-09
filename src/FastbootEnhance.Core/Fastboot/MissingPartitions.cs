using System;
using System.Collections.Generic;

namespace FastbootEnhance.Core.Fastboot
{
    /// <summary>
    /// Partitions an update writes that the phone does not have. Those the update places in
    /// super can be created in fastbootd ("create-logical-partition"), which then sizes them
    /// to their image; the others cannot be made from fastboot.
    /// </summary>
    public sealed class MissingPartitions
    {
        MissingPartitions(IReadOnlyList<string> create, IReadOnlyList<string> createFrom, IReadOnlyList<string> unknown)
        {
            Create = create;
            CreateFrom = createFrom;
            Unknown = unknown;
        }

        /// <summary>Logical partitions to create, with the current slot: "odm_dlkm_a".</summary>
        public IReadOnlyList<string> Create { get; }

        /// <summary>The update's names for <see cref="Create"/>, in the same order: "odm_dlkm".</summary>
        public IReadOnlyList<string> CreateFrom { get; }

        /// <summary>Missing partitions that cannot be created from fastboot.</summary>
        public IReadOnlyList<string> Unknown { get; }

        public bool None => Create.Count == 0 && Unknown.Count == 0;

        /// <param name="data">What the phone reported.</param>
        /// <param name="written">The partitions the update writes.</param>
        /// <param name="logical">Those of them the update places in super.</param>
        public static MissingPartitions Find(FastbootData data, IEnumerable<string> written, ICollection<string> logical)
        {
            List<string> create = new List<string>();
            List<string> createFrom = new List<string>();
            List<string> unknown = new List<string>();

            foreach (string name in written)
            {
                if (data.partition_size.ContainsKey(name))
                    continue;
                if (data.current_slot != null && data.partition_size.ContainsKey(name + "_" + data.current_slot))
                    continue;

                // Only fastbootd edits super, and only partitions the update says live there.
                if (data.fastbootd && logical.Contains(name))
                {
                    create.Add(data.current_slot != null ? name + "_" + data.current_slot : name);
                    createFrom.Add(name);
                }
                else
                {
                    unknown.Add(name);
                }
            }

            return new MissingPartitions(create, createFrom, unknown);
        }
    }
}
