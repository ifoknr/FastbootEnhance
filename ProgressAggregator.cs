using System;
using System.Collections.Generic;
using FastbootEnhance.Core.Payload;

namespace FastbootEnhance
{
    /// <summary>
    /// Folds the byte counts that several extraction workers report into one overall
    /// percentage and one percentage per partition. Both only ever move forward, and a
    /// callback fires only when a whole percent changes, so a payload with tens of thousands
    /// of operations does not flood the UI thread with updates.
    /// </summary>
    sealed class ProgressAggregator : IProgress<ExtractionProgress>
    {
        readonly object gate = new object();
        readonly Dictionary<string, long> written = new Dictionary<string, long>(StringComparer.Ordinal);
        readonly Dictionary<string, int> partitionPercent = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly long totalBytes;
        readonly Action<int> onOverall;
        readonly Action<string, int> onPartition;
        long done;
        int lastOverall = -1;

        public ProgressAggregator(long totalBytes, Action<int> onOverall, Action<string, int> onPartition = null)
        {
            this.totalBytes = Math.Max(1, totalBytes);
            this.onOverall = onOverall;
            this.onPartition = onPartition;
        }

        public void Report(ExtractionProgress value)
        {
            // Callbacks run inside the lock so their (non-blocking) dispatcher posts are queued
            // in the order the percentages were computed.
            lock (gate)
            {
                long previous;
                written.TryGetValue(value.Partition, out previous);
                done += value.BytesWritten - previous;
                written[value.Partition] = value.BytesWritten;

                int overall = (int)Math.Min(100, Math.Max(0, done * 100 / totalBytes));
                if (overall > lastOverall)
                {
                    lastOverall = overall;
                    if (onOverall != null)
                        onOverall(overall);
                }

                if (onPartition == null)
                    return;

                int percent = value.TotalBytes > 0
                    ? (int)Math.Min(100, value.BytesWritten * 100 / value.TotalBytes)
                    : 100;
                int seen;
                if (!partitionPercent.TryGetValue(value.Partition, out seen) || percent > seen)
                {
                    partitionPercent[value.Partition] = percent;
                    onPartition(value.Partition, percent);
                }
            }
        }
    }
}
