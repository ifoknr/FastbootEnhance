using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FastbootEnhance.Core;
using FastbootEnhance.Core.Payload;

namespace FastbootEnhance.PayloadTool
{
    /// <summary>
    /// Command line front end for the payload core. It exists so the extraction path can be
    /// exercised and measured without the Windows UI.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return 2;
            }

            string command = args[0].ToLowerInvariant();
            string[] rest = args.Skip(1).ToArray();

            try
            {
                switch (command)
                {
                    case "info":
                        return Info(rest);
                    case "extract":
                        return Extract(rest);
                    case "verify":
                        return Verify(rest);
                    case "imginfo":
                        return ImageCommands.ImgInfo(rest);
                    case "simg2img":
                        return ImageCommands.Simg2Img(rest);
                    case "img2simg":
                        return ImageCommands.Img2Simg(rest);
                    case "lpunpack":
                        return ImageCommands.LpUnpack(rest);
                    case "mksuper":
                        return ImageCommands.MkSuper(rest);
                    case "-h":
                    case "--help":
                    case "help":
                        Usage();
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown command: " + command);
                        Usage();
                        return 2;
                }
            }
            catch (PayloadFormatException e)
            {
                Console.Error.WriteLine("payload error: " + e.Message);
                return 1;
            }
            catch (PayloadExtractionException e)
            {
                Console.Error.WriteLine("extraction error on " + e.Partition + ": " + e.Message);
                return 1;
            }
            catch (FileNotFoundException e)
            {
                Console.Error.WriteLine("not found: " + e.FileName);
                return 1;
            }
            catch (InvalidDataException e)
            {
                Console.Error.WriteLine("image error: " + e.Message);
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("cancelled");
                return 130;
            }
            catch (IOException e)
            {
                Console.Error.WriteLine("i/o error: " + e.Message);
                return 1;
            }
            catch (UnauthorizedAccessException e)
            {
                Console.Error.WriteLine("access denied: " + e.Message);
                return 1;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("unexpected error: " + e);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("fbe-payload - inspect and extract Android A/B update payloads");
            Console.WriteLine();
            Console.WriteLine("  info    <payload.bin|ota.zip>");
            Console.WriteLine("  extract <payload.bin|ota.zip> -o <dir> [-p name ...] [-j N] [--no-verify] [--force]");
            Console.WriteLine("  verify  <payload.bin|ota.zip> [-p name ...] [-j N]");
            ImageCommands.Usage();
            Console.WriteLine();
            Console.WriteLine("  -o  output directory");
            Console.WriteLine("  -p  only these partitions (default: all)");
            Console.WriteLine("  -j  worker threads (default: one per core)");
            Console.WriteLine("  --no-verify  skip SHA-256 checks");
            Console.WriteLine("  --force      carry on past operations that cannot be applied");
        }

        // ---------- info ----------

        static int Info(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("info needs a payload path");
                return 2;
            }

            string path = args[0];
            string temp = Path.Combine(Path.GetTempPath(), "fbe-payload-" + Guid.NewGuid().ToString("N"));

            try
            {
                using (PayloadFile payload = PayloadFile.Open(path, temp))
                {
                    Console.WriteLine("file            " + Path.GetFileName(payload.Source.ContainerPath));
                    Console.WriteLine("container       " + (payload.Source.FromZip
                        ? "OTA zip, payload " + (payload.Source.ReadInPlace ? "read in place" : "unpacked to temp")
                        : "raw payload.bin"));
                    Console.WriteLine("payload size    " + ByteSize.Format(payload.Source.Length));
                    Console.WriteLine("format version  " + payload.FileFormatVersion);
                    Console.WriteLine("minor version   " + payload.MinorVersion);
                    Console.WriteLine("block size      " + payload.BlockSize);
                    Console.WriteLine("data region     " + ByteSize.Format(payload.DataLength) + " at offset " + payload.DataStart);
                    Console.WriteLine("signed          metadata=" + (payload.MetadataSignature != null)
                        + " payload=" + (payload.PayloadSignature != null));
                    Console.WriteLine("package kind    " + (payload.IsIncremental ? "incremental" : "full"));
                    Console.WriteLine("partitions      " + payload.Partitions.Count);
                    Console.WriteLine();

                    PrintPartitionTable(payload);
                    Console.WriteLine();
                    PrintCodecBreakdown(payload);
                }
            }
            finally
            {
                TryRemoveDirectory(temp);
            }

            return 0;
        }

        static void PrintPartitionTable(PayloadFile payload)
        {
            if (payload.Partitions.Count == 0)
            {
                Console.WriteLine("this payload's manifest lists no partitions");
                return;
            }

            int nameWidth = Math.Max(9, payload.Partitions.Max(p => p.Name.Length));

            Console.WriteLine(
                "PARTITION".PadRight(nameWidth) + "  " +
                "SIZE".PadLeft(10) + "  " +
                "OPS".PadLeft(5) + "  " +
                "CODECS".PadRight(22) + "  " +
                "SHA-256".PadRight(16) + "  STATE");
            Console.WriteLine(new string('-', nameWidth + 70));

            foreach (PayloadPartitionInfo part in payload.Partitions)
            {
                string codecs = string.Join(",", part.Codecs);
                if (codecs.Length > 22)
                    codecs = codecs.Substring(0, 21) + "+";

                string hash = string.IsNullOrEmpty(part.ExpectedSha256)
                    ? "(none)"
                    : part.ExpectedSha256.Length > 16 ? part.ExpectedSha256.Substring(0, 16) : part.ExpectedSha256;

                string state;
                switch (part.Support)
                {
                    case OperationSupportKind.Supported:
                        state = "ready";
                        break;
                    case OperationSupportKind.RequiresSource:
                        state = "needs source image";
                        break;
                    default:
                        state = "unsupported";
                        break;
                }

                Console.WriteLine(
                    part.Name.PadRight(nameWidth) + "  " +
                    ByteSize.Format(part.UnpackedSize).PadLeft(10) + "  " +
                    part.OperationCount.ToString().PadLeft(5) + "  " +
                    codecs.PadRight(22) + "  " +
                    hash.PadRight(16) + "  " + state);
            }
        }

        static void PrintCodecBreakdown(PayloadFile payload)
        {
            IReadOnlyList<KeyValuePair<string, int>> counts = payload.OperationCounts();
            int total = counts.Sum(p => p.Value);

            Console.WriteLine("OPERATION TYPES (" + total + " operations)");
            foreach (KeyValuePair<string, int> pair in counts)
            {
                double share = total > 0 ? 100.0 * pair.Value / total : 0;
                int bar = (int)Math.Round(share / 4);
                Console.WriteLine(
                    "  " + pair.Key.PadRight(18) +
                    pair.Value.ToString().PadLeft(6) + "  " +
                    share.ToString("F1").PadLeft(5) + "%  " +
                    new string('#', bar));
            }
        }

        // ---------- extract and verify ----------

        static int Extract(string[] args)
        {
            Options options = Options.Parse(args);
            if (options.Path == null)
            {
                Console.Error.WriteLine("extract needs a payload path");
                return 2;
            }
            if (options.OutputDirectory == null)
            {
                Console.Error.WriteLine("extract needs -o <dir>");
                return 2;
            }

            return Run(options, options.OutputDirectory);
        }

        static int Verify(string[] args)
        {
            Options options = Options.Parse(args);
            if (options.Path == null)
            {
                Console.Error.WriteLine("verify needs a payload path");
                return 2;
            }

            string scratch = Path.Combine(Path.GetTempPath(), "fbe-verify-" + Guid.NewGuid().ToString("N"));
            try
            {
                return Run(options, scratch);
            }
            finally
            {
                TryRemoveDirectory(scratch);
            }
        }

        static int Run(Options options, string outputDirectory)
        {
            string temp = Path.Combine(Path.GetTempPath(), "fbe-payload-" + Guid.NewGuid().ToString("N"));

            try
            {
                using (PayloadFile payload = PayloadFile.Open(options.Path, temp))
                {
                    ExtractionOptions extraction = new ExtractionOptions
                    {
                        VerifyOperationHashes = options.Verify,
                        VerifyImageHash = options.Verify,
                        IgnoreUnsupportedOperations = options.Force,
                        MaxDegreeOfParallelism = options.Workers ?? Environment.ProcessorCount
                    };

                    List<string> names = options.Partitions.Count > 0
                        ? options.Partitions
                        : payload.Partitions.Select(p => p.Name).ToList();

                    Console.WriteLine("payload    " + Path.GetFileName(options.Path)
                        + (payload.Source.FromZip && payload.Source.ReadInPlace ? "  (read in place from zip)" : ""));
                    Console.WriteLine("partitions " + names.Count + " of " + payload.Partitions.Count);
                    Console.WriteLine("workers    " + extraction.MaxDegreeOfParallelism);
                    Console.WriteLine("verify     " + (options.Verify ? "sha-256 per operation and per image" : "off"));
                    Console.WriteLine();

                    Stopwatch watch = Stopwatch.StartNew();
                    ExtractionReport report = PayloadExtractor.ExtractAll(
                        payload, outputDirectory, names, extraction, null, CancellationToken.None);
                    watch.Stop();

                    foreach (PartitionResult result in report.Results)
                    {
                        if (result.Succeeded)
                        {
                            double seconds = Math.Max(result.Elapsed.TotalSeconds, 0.0001);
                            Console.WriteLine("  ok    " + result.Partition.PadRight(16)
                                + ByteSize.Format(result.Size).PadLeft(10)
                                + "  " + result.Elapsed.TotalMilliseconds.ToString("F0").PadLeft(6) + " ms"
                                + "  " + (result.Size / seconds / (1024 * 1024)).ToString("F0").PadLeft(5) + " MB/s");
                        }
                        else
                        {
                            Console.WriteLine("  FAIL  " + result.Partition.PadRight(16) + result.Error);
                        }
                    }

                    Console.WriteLine();
                    double total = Math.Max(report.Elapsed.TotalSeconds, 0.0001);
                    Console.WriteLine(report.SucceededCount + " ok, " + report.FailedCount + " failed, "
                        + ByteSize.Format(report.TotalBytes) + " in " + report.Elapsed.TotalSeconds.ToString("F2") + " s ("
                        + (report.TotalBytes / total / (1024 * 1024)).ToString("F0") + " MB/s aggregate)");

                    return report.AllSucceeded ? 0 : 1;
                }
            }
            finally
            {
                TryRemoveDirectory(temp);
            }
        }

        // ---------- plumbing ----------

        sealed class Options
        {
            public string Path;
            public string OutputDirectory;
            public List<string> Partitions = new List<string>();
            public int? Workers;
            public bool Verify = true;
            public bool Force;

            public static Options Parse(string[] args)
            {
                Options options = new Options();

                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    switch (arg)
                    {
                        case "-o":
                        case "--output":
                            options.OutputDirectory = ++i < args.Length ? args[i] : null;
                            break;
                        case "-j":
                        case "--workers":
                            if (++i < args.Length && int.TryParse(args[i], out int workers))
                                options.Workers = Math.Max(1, workers);
                            break;
                        case "-p":
                        case "--partitions":
                            while (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                                options.Partitions.Add(args[++i]);
                            break;
                        case "--no-verify":
                            options.Verify = false;
                            break;
                        case "--force":
                            options.Force = true;
                            break;
                        default:
                            if (options.Path == null && !arg.StartsWith("-", StringComparison.Ordinal))
                                options.Path = arg;
                            break;
                    }
                }

                return options;
            }
        }

        static void TryRemoveDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
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
