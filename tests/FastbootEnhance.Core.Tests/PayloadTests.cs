using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using ChromeosUpdateEngine;
using FastbootEnhance.Core.Payload;
using Xunit;

namespace FastbootEnhance.Core.Tests
{
    public sealed class PayloadTests : IDisposable
    {
        const uint BlockSize = 4096;

        readonly string workDir;

        public PayloadTests()
        {
            workDir = Path.Combine(Path.GetTempPath(), "fbe-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(workDir, true);
            }
            catch (IOException)
            {
            }
        }

        // ---------- helpers ----------

        static byte[] Data(int length, int seed)
        {
            byte[] bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        static byte[] Concat(params byte[][] chunks)
        {
            byte[] result = new byte[chunks.Sum(c => c.Length)];
            int at = 0;
            foreach (byte[] chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, result, at, chunk.Length);
                at += chunk.Length;
            }
            return result;
        }

        static BlockRange[] At(params (ulong start, ulong count)[] ranges)
        {
            return ranges.Select(r => new BlockRange(r.start, r.count)).ToArray();
        }

        /// <summary>Builds a one-partition payload whose single operation uses the given codec.</summary>
        string SingleCodecPayload(InstallOperation.Types.Type type, byte[] image, string name = "system")
        {
            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition(name, image);
            part.Ops.Add(new OpSpec
            {
                Type = type,
                Content = image,
                Dst = At((0, (ulong)(image.Length / BlockSize)))
            });
            return builder.WriteToFile(workDir, Guid.NewGuid().ToString("N") + ".bin");
        }

        byte[] ExtractToBytes(string payloadPath, string partition, ExtractionOptions options = null)
        {
            using (PayloadFile payload = PayloadFile.Open(payloadPath, workDir))
            using (MemoryStream output = new MemoryStream())
            {
                PartitionExtractor.Extract(payload, partition, output, options);
                return output.ToArray();
            }
        }

        // ---------- header and manifest ----------

        [Fact]
        public void Reads_header_manifest_and_signatures()
        {
            byte[] image = Data((int)BlockSize * 4, 1);
            string path = SingleCodecPayload(InstallOperation.Types.Type.Zstd, image);

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                Assert.Equal(2UL, payload.FileFormatVersion);
                Assert.Equal(BlockSize, payload.BlockSize);
                Assert.Equal(9u, payload.MinorVersion);
                Assert.Single(payload.Partitions);
                Assert.Equal("system", payload.Partitions[0].Name);
                Assert.Equal(image.Length, payload.Partitions[0].UnpackedSize);
                Assert.NotNull(payload.PayloadSignature);
                Assert.False(payload.IsIncremental);

                using (SHA256 sha = SHA256.Create())
                {
                    string expected = BitConverter
                        .ToString(sha.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
                    Assert.Equal(expected, payload.Partitions[0].ExpectedSha256);
                }
            }
        }

        [Fact]
        public void Rejects_a_file_that_is_not_a_payload()
        {
            string path = Path.Combine(workDir, "random.bin");
            File.WriteAllBytes(path, Data(8192, 2));

            PayloadFormatException error = Assert.Throws<PayloadFormatException>(
                () => PayloadFile.Open(path, workDir));
            Assert.Contains("CrAU", error.Message);
        }

        [Fact]
        public void Rejects_a_truncated_payload()
        {
            byte[] image = Data((int)BlockSize * 4, 3);
            string full = SingleCodecPayload(InstallOperation.Types.Type.Replace, image);

            byte[] bytes = File.ReadAllBytes(full);
            string path = Path.Combine(workDir, "cut.bin");
            File.WriteAllBytes(path, bytes.Take(bytes.Length / 3).ToArray());

            Assert.ThrowsAny<Exception>(() =>
            {
                using (PayloadFile payload = PayloadFile.Open(path, workDir))
                using (MemoryStream output = new MemoryStream())
                    PartitionExtractor.Extract(payload, "system", output);
            });
        }

        // ---------- every supported codec ----------

        [Theory]
        [InlineData(InstallOperation.Types.Type.Replace)]
        [InlineData(InstallOperation.Types.Type.ReplaceBz)]
        [InlineData(InstallOperation.Types.Type.ReplaceXz)]
        [InlineData(InstallOperation.Types.Type.Zstd)]
        public void Extracts_each_supported_codec_byte_for_byte(InstallOperation.Types.Type type)
        {
            byte[] image = Data((int)BlockSize * 16, 11);
            string path = SingleCodecPayload(type, image);

            byte[] produced = ExtractToBytes(path, "system");

            Assert.Equal(image.Length, produced.Length);
            Assert.True(image.SequenceEqual(produced), type + " did not round-trip");
        }

        [Fact]
        public void Zstd_is_recognised_as_operation_type_fourteen()
        {
            Assert.Equal(14, (int)InstallOperation.Types.Type.Zstd);
            Assert.Equal(11, (int)InstallOperation.Types.Type.Zucchini);
            Assert.Equal(12, (int)InstallOperation.Types.Type.Lz4DiffBsdiff);
            Assert.Equal(13, (int)InstallOperation.Types.Type.Lz4DiffPuffdiff);

            Assert.True(OperationSupport.Describe(InstallOperation.Types.Type.Zstd).IsSupported);
            Assert.Equal("zstd", OperationSupport.Describe(InstallOperation.Types.Type.Zstd).Codec);
        }

        // ---------- the multiple-extent case the old reader refused ----------

        [Fact]
        public void Extracts_one_operation_across_several_out_of_order_extents()
        {
            byte[] blockA = Data((int)BlockSize, 21);
            byte[] blockB = Data((int)BlockSize, 22);
            byte[] blockC = Data((int)BlockSize, 23);

            // The operation's data fills its extents in order: block 2 first, then blocks 0 and 1.
            byte[] content = Concat(blockA, blockB, blockC);
            byte[] expected = Concat(blockB, blockC, blockA);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("product", expected);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = content,
                Dst = At((2, 1), (0, 2))
            });
            string path = builder.WriteToFile(workDir, "extents.bin");

            byte[] produced = ExtractToBytes(path, "product");

            Assert.Equal(expected.Length, produced.Length);
            Assert.True(expected.SequenceEqual(produced));
        }

        [Fact]
        public void Extracts_several_operations_into_one_image()
        {
            byte[] first = Data((int)BlockSize * 2, 31);
            byte[] second = Data((int)BlockSize * 3, 32);
            byte[] third = Data((int)BlockSize, 33);
            byte[] expected = Concat(first, second, third);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("vendor", expected);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = first,
                Dst = At((0, 2))
            });
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.ReplaceXz,
                Content = second,
                Dst = At((2, 3))
            });
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.ReplaceBz,
                Content = third,
                Dst = At((5, 1))
            });
            string path = builder.WriteToFile(workDir, "mixed.bin");

            byte[] produced = ExtractToBytes(path, "vendor");

            Assert.True(expected.SequenceEqual(produced));

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                PayloadPartitionInfo info = payload.FindPartition("vendor");
                Assert.Equal(3, info.OperationCount);
                Assert.Equal(new[] { "zstd", "xz", "bzip2" }, info.Codecs.ToArray());
                Assert.True(info.CanExtract);
            }
        }

        [Fact]
        public void Zero_operation_leaves_blocks_zeroed()
        {
            byte[] head = Data((int)BlockSize, 41);
            byte[] expected = Concat(head, new byte[BlockSize * 2], head);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("boot", expected);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = head,
                Dst = At((0, 1))
            });
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zero,
                Dst = At((1, 2))
            });
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = head,
                Dst = At((3, 1))
            });
            string path = builder.WriteToFile(workDir, "zero.bin");

            byte[] produced = ExtractToBytes(path, "boot");

            Assert.True(expected.SequenceEqual(produced));
        }

        // ---------- integrity checking ----------

        [Fact]
        public void Detects_a_corrupted_operation_blob()
        {
            byte[] image = Data((int)BlockSize * 8, 51);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = image,
                Dst = At((0, 8)),
                CorruptBlob = true
            });
            string path = builder.WriteToFile(workDir, "corrupt.bin");

            PayloadExtractionException error = Assert.Throws<PayloadExtractionException>(
                () => ExtractToBytes(path, "system"));
            Assert.Contains("hash", error.Message);
            Assert.Equal("system", error.Partition);
        }

        [Fact]
        public void Detects_an_image_that_does_not_match_the_manifest_hash()
        {
            byte[] image = Data((int)BlockSize * 4, 61);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.WrongHash = true;
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = image,
                Dst = At((0, 4))
            });
            string path = builder.WriteToFile(workDir, "badhash.bin");

            PayloadExtractionException error = Assert.Throws<PayloadExtractionException>(
                () => ExtractToBytes(path, "system"));
            Assert.Contains("SHA-256", error.Message);
        }

        [Fact]
        public void Skips_hash_checks_when_asked_to()
        {
            byte[] image = Data((int)BlockSize * 4, 71);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.WrongHash = true;
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = image,
                Dst = At((0, 4))
            });
            string path = builder.WriteToFile(workDir, "nocheck.bin");

            byte[] produced = ExtractToBytes(
                path, "system", new ExtractionOptions { VerifyImageHash = false });

            Assert.True(image.SequenceEqual(produced));
        }

        // ---------- operations we cannot apply ----------

        [Fact]
        public void Refuses_a_binary_diff_operation_and_says_why()
        {
            byte[] image = Data((int)BlockSize * 2, 81);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zucchini,
                Content = image,
                Dst = At((0, 2))
            });
            string path = builder.WriteToFile(workDir, "zucchini.bin");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                Assert.True(payload.IsIncremental);
                PayloadPartitionInfo info = payload.FindPartition("system");
                Assert.False(info.CanExtract);
                Assert.Equal(OperationSupportKind.Unsupported, info.Support);
                Assert.Contains("zucchini", info.UnsupportedReason);

                using (MemoryStream output = new MemoryStream())
                {
                    PayloadExtractionException error = Assert.Throws<PayloadExtractionException>(
                        () => PartitionExtractor.Extract(payload, "system", output));
                    Assert.Contains("zucchini", error.Message);
                }
            }
        }

        [Fact]
        public void Source_operations_are_reported_as_needing_the_previous_image()
        {
            OperationSupportInfo copy = OperationSupport.Describe(InstallOperation.Types.Type.SourceCopy);
            Assert.Equal(OperationSupportKind.RequiresSource, copy.Kind);
            Assert.Contains("previous contents", copy.Reason);
        }

        [Fact]
        public void Can_push_past_unsupported_operations_when_told_to()
        {
            byte[] good = Data((int)BlockSize, 91);
            byte[] expected = Concat(good, new byte[BlockSize]);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", expected);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = good,
                Dst = At((0, 1))
            });
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Puffdiff,
                Content = good,
                Dst = At((1, 1))
            });
            string path = builder.WriteToFile(workDir, "partial.bin");

            byte[] produced = ExtractToBytes(
                path, "system", new ExtractionOptions { IgnoreUnsupportedOperations = true });

            Assert.Equal(expected.Length, produced.Length);
            Assert.True(produced.Take((int)BlockSize).SequenceEqual(good));
            Assert.True(produced.Skip((int)BlockSize).All(b => b == 0));
        }

        // ---------- zip containers ----------

        [Fact]
        public void Reads_a_stored_payload_in_place_inside_an_ota_zip()
        {
            byte[] image = Data((int)BlockSize * 8, 101);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = image,
                Dst = At((0, 8))
            });
            string zipPath = builder.WriteToZip(workDir, "ota-stored.zip", stored: true);

            using (PayloadSource source = PayloadSource.Open(zipPath, workDir))
            {
                Assert.True(source.FromZip);
                Assert.True(source.ReadInPlace);

                using (PayloadFile payload = PayloadFile.FromSource(source, false))
                using (MemoryStream output = new MemoryStream())
                {
                    PartitionExtractor.Extract(payload, "system", output);
                    Assert.True(image.SequenceEqual(output.ToArray()));
                }
            }

            // Nothing should have been unpacked next to the zip.
            Assert.Empty(Directory.GetFiles(workDir, "payload.bin.*"));
        }

        [Fact]
        public void Unpacks_a_deflated_payload_from_a_zip_and_still_extracts()
        {
            byte[] image = Data((int)BlockSize * 8, 111);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = image,
                Dst = At((0, 8))
            });
            string zipPath = builder.WriteToZip(workDir, "ota-deflated.zip", stored: false);

            using (PayloadSource source = PayloadSource.Open(zipPath, workDir))
            {
                Assert.True(source.FromZip);
                Assert.False(source.ReadInPlace);

                using (PayloadFile payload = PayloadFile.FromSource(source, false))
                using (MemoryStream output = new MemoryStream())
                {
                    PartitionExtractor.Extract(payload, "system", output);
                    Assert.True(image.SequenceEqual(output.ToArray()));
                }
            }
        }

        [Fact]
        public void Reports_a_zip_with_no_payload_entry()
        {
            string zipPath = Path.Combine(workDir, "empty.zip");
            using (FileStream file = new FileStream(zipPath, FileMode.Create))
            using (System.IO.Compression.ZipArchive archive =
                new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("readme.txt");
            }

            PayloadFormatException error = Assert.Throws<PayloadFormatException>(
                () => PayloadSource.Open(zipPath, workDir));
            Assert.Contains("payload.bin", error.Message);
        }

        // ---------- parallel extraction ----------

        [Fact]
        public void Extracts_many_partitions_in_parallel_to_disk()
        {
            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            Dictionary<string, byte[]> expected = new Dictionary<string, byte[]>();

            for (int i = 0; i < 8; i++)
            {
                string name = "part" + i;
                byte[] image = Data((int)BlockSize * 32, 200 + i);
                expected[name] = image;

                PartSpec part = builder.AddPartition(name, image);
                part.Ops.Add(new OpSpec
                {
                    Type = InstallOperation.Types.Type.Zstd,
                    Content = image,
                    Dst = At((0, 32))
                });
            }

            string path = builder.WriteToFile(workDir, "many.bin");
            string outDir = Path.Combine(workDir, "out");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                ExtractionReport report = PayloadExtractor.ExtractAll(
                    payload, outDir, options: new ExtractionOptions { MaxDegreeOfParallelism = 4 });

                Assert.True(report.AllSucceeded, string.Join("; ",
                    report.Results.Where(r => !r.Succeeded).Select(r => r.Partition + ": " + r.Error)));
                Assert.Equal(8, report.SucceededCount);
                Assert.Equal(expected.Values.Sum(v => v.Length), report.TotalBytes);
            }

            foreach (KeyValuePair<string, byte[]> pair in expected)
            {
                byte[] onDisk = File.ReadAllBytes(Path.Combine(outDir, pair.Key + ".img"));
                Assert.True(pair.Value.SequenceEqual(onDisk), pair.Key + " differs on disk");
            }
        }

        [Fact]
        public void A_failing_partition_does_not_sink_the_others()
        {
            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };

            byte[] goodImage = Data((int)BlockSize * 4, 301);
            PartSpec good = builder.AddPartition("good", goodImage);
            good.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = goodImage,
                Dst = At((0, 4))
            });

            byte[] badImage = Data((int)BlockSize * 4, 302);
            PartSpec bad = builder.AddPartition("bad", badImage);
            bad.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zucchini,
                Content = badImage,
                Dst = At((0, 4))
            });

            string path = builder.WriteToFile(workDir, "partial-fail.bin");
            string outDir = Path.Combine(workDir, "out2");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                ExtractionReport report = PayloadExtractor.ExtractAll(payload, outDir);

                Assert.Equal(1, report.SucceededCount);
                Assert.Equal(1, report.FailedCount);

                PartitionResult badResult = report.Results.Single(r => r.Partition == "bad");
                Assert.Contains("zucchini", badResult.Error);
                Assert.False(File.Exists(badResult.OutputPath));

                PartitionResult goodResult = report.Results.Single(r => r.Partition == "good");
                Assert.True(goodResult.Succeeded);
                Assert.True(goodImage.SequenceEqual(File.ReadAllBytes(goodResult.OutputPath)));
            }
        }

        [Fact]
        public void Cancellation_stops_extraction_and_removes_the_part_file()
        {
            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            byte[] chunk = Data((int)BlockSize, 401);
            byte[] image = new byte[BlockSize * 64];

            PartSpec part = builder.AddPartition("big", image);
            for (ulong block = 0; block < 64; block++)
            {
                part.Ops.Add(new OpSpec
                {
                    Type = InstallOperation.Types.Type.Zero,
                    Dst = At((block, 1))
                });
            }

            string path = builder.WriteToFile(workDir, "cancel.bin");
            string outDir = Path.Combine(workDir, "out3");

            using (CancellationTokenSource cts = new CancellationTokenSource())
            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                IProgress<ExtractionProgress> progress = new Progress<ExtractionProgress>(_ => { });
                cts.Cancel();

                Assert.ThrowsAny<OperationCanceledException>(() =>
                    PayloadExtractor.ExtractAll(
                        payload, outDir, progress: progress, cancellationToken: cts.Token));
            }

            Assert.False(File.Exists(Path.Combine(outDir, "big.img")));
            Assert.True(chunk.Length > 0);
        }

        [Fact]
        public void Reports_progress_while_extracting()
        {
            byte[] image = Data((int)BlockSize * 16, 501);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("system", image);
            for (ulong block = 0; block < 16; block++)
            {
                byte[] slice = new byte[BlockSize];
                Buffer.BlockCopy(image, (int)(block * BlockSize), slice, 0, (int)BlockSize);
                part.Ops.Add(new OpSpec
                {
                    Type = InstallOperation.Types.Type.Zstd,
                    Content = slice,
                    Dst = At((block, 1))
                });
            }

            string path = builder.WriteToFile(workDir, "progress.bin");

            List<ExtractionProgress> seen = new List<ExtractionProgress>();
            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            using (MemoryStream output = new MemoryStream())
            {
                PartitionExtractor.Extract(
                    payload, "system", output,
                    progress: new SynchronousProgress(seen.Add));

                Assert.True(image.SequenceEqual(output.ToArray()));
            }

            Assert.Equal(16, seen.Count);
            Assert.Equal(16, seen.Last().OperationsDone);
            Assert.Equal(image.Length, seen.Last().BytesWritten);
            Assert.Equal(1.0, seen.Last().Fraction, 3);
        }

        [Fact]
        public void A_full_package_declaring_minor_version_zero_still_decodes_zstd()
        {
            // Real full OTAs report minor_version 0, so nothing may gate codec support on it.
            byte[] image = Data((int)BlockSize * 8, 701);

            TestPayloadBuilder builder = new TestPayloadBuilder
            {
                BlockSize = BlockSize,
                MinorVersion = 0
            };
            PartSpec part = builder.AddPartition("system", image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = image,
                Dst = At((0, 8))
            });
            string path = builder.WriteToFile(workDir, "minor0.bin");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                Assert.Equal(0u, payload.MinorVersion);
                Assert.False(payload.IsIncremental);
            }

            Assert.True(image.SequenceEqual(ExtractToBytes(path, "system")));
        }

        [Fact]
        public void A_partition_without_a_declared_hash_or_size_still_extracts()
        {
            byte[] image = Data((int)BlockSize * 4, 711);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition("odm", image);
            part.OmitSize = true;
            part.OmitHash = true;
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = image,
                Dst = At((0, 4))
            });
            string path = builder.WriteToFile(workDir, "nometa.bin");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                PayloadPartitionInfo info = payload.FindPartition("odm");
                // With no declared size the reader works it out from the extents.
                Assert.Equal(image.Length, info.UnpackedSize);
                Assert.Null(info.ExpectedSha256);
            }

            Assert.True(image.SequenceEqual(ExtractToBytes(path, "odm")));
        }

        [Theory]
        [InlineData("../../escaped")]
        [InlineData("..\\..\\escaped")]
        [InlineData("sub/system")]
        [InlineData("..")]
        public void A_partition_name_that_escapes_the_output_folder_is_refused(string hostileName)
        {
            byte[] image = Data((int)BlockSize * 2, 801);

            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };
            PartSpec part = builder.AddPartition(hostileName, image);
            part.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = image,
                Dst = At((0, 2))
            });
            string path = builder.WriteToFile(workDir, "hostile.bin");

            string outDir = Path.Combine(workDir, "safe", "out");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                ExtractionReport report = PayloadExtractor.ExtractAll(payload, outDir);

                Assert.Equal(1, report.FailedCount);
                Assert.Contains("plain file name", report.Results[0].Error);
            }

            // Nothing may have been created above the chosen folder.
            Assert.Empty(Directory.GetFiles(Path.Combine(workDir, "safe"), "*.img",
                SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workDir, "escaped*", SearchOption.TopDirectoryOnly));
        }

        [Fact]
        public void A_hostile_partition_name_does_not_stop_the_good_ones()
        {
            TestPayloadBuilder builder = new TestPayloadBuilder { BlockSize = BlockSize };

            byte[] good = Data((int)BlockSize * 2, 811);
            PartSpec goodPart = builder.AddPartition("system", good);
            goodPart.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Zstd,
                Content = good,
                Dst = At((0, 2))
            });

            byte[] bad = Data((int)BlockSize * 2, 812);
            PartSpec badPart = builder.AddPartition("../escape", bad);
            badPart.Ops.Add(new OpSpec
            {
                Type = InstallOperation.Types.Type.Replace,
                Content = bad,
                Dst = At((0, 2))
            });

            string path = builder.WriteToFile(workDir, "mixed-hostile.bin");
            string outDir = Path.Combine(workDir, "mixedout");

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            {
                ExtractionReport report = PayloadExtractor.ExtractAll(payload, outDir);

                Assert.Equal(1, report.SucceededCount);
                Assert.Equal(1, report.FailedCount);
                Assert.True(good.SequenceEqual(File.ReadAllBytes(Path.Combine(outDir, "system.img"))));
            }
        }

        [Fact]
        public void Unknown_partition_name_is_reported_clearly()
        {
            byte[] image = Data((int)BlockSize * 2, 601);
            string path = SingleCodecPayload(InstallOperation.Types.Type.Replace, image);

            using (PayloadFile payload = PayloadFile.Open(path, workDir))
            using (MemoryStream output = new MemoryStream())
            {
                PayloadExtractionException error = Assert.Throws<PayloadExtractionException>(
                    () => PartitionExtractor.Extract(payload, "nope", output));
                Assert.Contains("no such partition", error.Message);
            }
        }

        sealed class SynchronousProgress : IProgress<ExtractionProgress>
        {
            readonly Action<ExtractionProgress> sink;

            public SynchronousProgress(Action<ExtractionProgress> sink)
            {
                this.sink = sink;
            }

            public void Report(ExtractionProgress value)
            {
                sink(value);
            }
        }
    }
}
