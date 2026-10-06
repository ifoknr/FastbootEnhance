using System;

namespace FastbootEnhance.Core.Payload
{
    public sealed class ExtractionOptions
    {
        /// <summary>Check each operation's data blob against the SHA-256 in the manifest.</summary>
        public bool VerifyOperationHashes { get; set; } = true;

        /// <summary>Check the finished image's size and SHA-256 against the manifest.</summary>
        public bool VerifyImageHash { get; set; } = true;

        /// <summary>
        /// Carry on past operations this build cannot apply, leaving their blocks zeroed.
        /// The result is an incomplete image, so the image hash check is skipped with it.
        /// </summary>
        public bool IgnoreUnsupportedOperations { get; set; }

        /// <summary>How many partitions to decode at once.</summary>
        public int MaxDegreeOfParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount);

        internal void Validate()
        {
            if (MaxDegreeOfParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), "must be at least 1");
        }
    }

    public struct ExtractionProgress
    {
        public string Partition { get; set; }
        public long BytesWritten { get; set; }
        public long TotalBytes { get; set; }
        public int OperationsDone { get; set; }
        public int OperationsTotal { get; set; }

        public double Fraction => TotalBytes > 0 ? (double)BytesWritten / TotalBytes : 0.0;
    }
}
