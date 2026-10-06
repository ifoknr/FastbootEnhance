using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FastbootEnhance.Core.Payload
{
    public sealed class PartitionResult
    {
        public string Partition { get; internal set; }
        public string OutputPath { get; internal set; }
        public long Size { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>Null when the partition extracted cleanly.</summary>
        public string Error { get; internal set; }

        public bool Succeeded => Error == null;
    }

    public sealed class ExtractionReport
    {
        public IReadOnlyList<PartitionResult> Results { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }

        public int SucceededCount => Results.Count(r => r.Succeeded);
        public int FailedCount => Results.Count(r => !r.Succeeded);
        public long TotalBytes => Results.Where(r => r.Succeeded).Sum(r => r.Size);
        public bool AllSucceeded => FailedCount == 0;
    }

    /// <summary>
    /// Extracts several partitions at once. Each worker opens its own reader over the payload,
    /// so decoding scales with the number of cores instead of running one partition at a time.
    /// </summary>
    public static class PayloadExtractor
    {
        const int WriteBufferSize = 1 << 20;

        public static ExtractionReport ExtractAll(
            PayloadFile payload,
            string outputDirectory,
            IEnumerable<string> partitionNames = null,
            ExtractionOptions options = null,
            IProgress<ExtractionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentNullException(nameof(outputDirectory));

            options = options ?? new ExtractionOptions();
            options.Validate();

            List<string> names = partitionNames != null
                ? partitionNames.ToList()
                : payload.Partitions.Select(p => p.Name).ToList();

            Directory.CreateDirectory(outputDirectory);

            ConcurrentDictionary<string, PartitionResult> collected =
                new ConcurrentDictionary<string, PartitionResult>(StringComparer.Ordinal);

            Stopwatch overall = Stopwatch.StartNew();

            ParallelOptions parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CancellationToken = cancellationToken
            };

            try
            {
                Parallel.ForEach(names, parallelOptions, name =>
                {
                    collected[name] = ExtractOne(payload, name, outputDirectory, options, progress, cancellationToken);
                });
            }
            catch (OperationCanceledException)
            {
                overall.Stop();
                throw;
            }

            overall.Stop();

            // Keep the manifest's order so the report lines up with what the user sees.
            List<PartitionResult> ordered = new List<PartitionResult>(names.Count);
            foreach (string name in names)
            {
                PartitionResult result;
                if (collected.TryGetValue(name, out result))
                    ordered.Add(result);
            }

            return new ExtractionReport { Results = ordered, Elapsed = overall.Elapsed };
        }

        /// <summary>
        /// Builds the output path for a partition. Partition names come out of the payload's
        /// manifest, which is untrusted input, so a name must be a plain file name and the
        /// result must stay inside the chosen directory.
        /// </summary>
        internal static string ResolveOutputPath(string outputDirectory, string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new PayloadExtractionException(name, "the payload names a partition with no name");

            if (name == "." || name == ".."
                || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new PayloadExtractionException(
                    name, "partition name \"" + name + "\" is not a plain file name");
            }

            string root = Path.GetFullPath(outputDirectory);
            if (root[root.Length - 1] != Path.DirectorySeparatorChar)
                root += Path.DirectorySeparatorChar;

            string full = Path.GetFullPath(Path.Combine(root, name + ".img"));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new PayloadExtractionException(
                    name, "partition name \"" + name + "\" would write outside the chosen folder");
            }

            return full;
        }

        static PartitionResult ExtractOne(
            PayloadFile payload,
            string name,
            string outputDirectory,
            ExtractionOptions options,
            IProgress<ExtractionProgress> progress,
            CancellationToken cancellationToken)
        {
            Stopwatch watch = Stopwatch.StartNew();
            string path;

            try
            {
                path = ResolveOutputPath(outputDirectory, name);
            }
            catch (Exception e)
            {
                watch.Stop();
                return new PartitionResult
                {
                    Partition = name,
                    OutputPath = null,
                    Size = 0,
                    Elapsed = watch.Elapsed,
                    Error = e.Message
                };
            }

            try
            {
                using (FileStream output = new FileStream(
                    path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, WriteBufferSize))
                {
                    long size = PartitionExtractor.Extract(
                        payload, name, output, options, progress, cancellationToken);

                    watch.Stop();
                    return new PartitionResult
                    {
                        Partition = name,
                        OutputPath = path,
                        Size = size,
                        Elapsed = watch.Elapsed
                    };
                }
            }
            catch (OperationCanceledException)
            {
                watch.Stop();
                TryDelete(path);
                throw;
            }
            catch (Exception e)
            {
                watch.Stop();
                TryDelete(path);
                return new PartitionResult
                {
                    Partition = name,
                    OutputPath = path,
                    Size = 0,
                    Elapsed = watch.Elapsed,
                    Error = e.Message
                };
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
