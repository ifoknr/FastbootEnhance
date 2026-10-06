using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using ChromeosUpdateEngine;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// Rebuilds one partition image from a payload's install operations.
    /// </summary>
    public static class PartitionExtractor
    {
        const int CopyBufferSize = 1 << 20;

        /// <summary>
        /// Writes the image for <paramref name="partitionName"/> into <paramref name="output"/>,
        /// which must be writable and seekable. Returns the number of bytes the image occupies.
        /// </summary>
        public static long Extract(
            PayloadFile payload,
            string partitionName,
            Stream output,
            ExtractionOptions options = null,
            IProgress<ExtractionProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (partitionName == null) throw new ArgumentNullException(nameof(partitionName));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!output.CanWrite) throw new ArgumentException("output must be writable", nameof(output));
            if (!output.CanSeek) throw new ArgumentException("output must be seekable", nameof(output));

            options = options ?? new ExtractionOptions();
            options.Validate();

            PartitionUpdate update = payload.FindUpdate(partitionName);
            if (update == null)
                throw new PayloadExtractionException(partitionName, "no such partition in this payload");

            PayloadPartitionInfo info = payload.FindPartition(partitionName);
            if (!info.CanExtract && !options.IgnoreUnsupportedOperations)
            {
                throw new PayloadExtractionException(
                    partitionName,
                    info.UnsupportedReason ?? "this partition uses operations that cannot be applied");
            }

            long expectedSize = info.UnpackedSize;
            output.SetLength(expectedSize);

            bool skippedAnything = false;
            long bytesWritten = 0;
            int operationsDone = 0;
            int operationsTotal = update.Operations.Count;

            byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            MemoryStream decoded = new MemoryStream();

            try
            {
                using (Stream source = payload.Source.OpenStream())
                using (SHA256 sha = SHA256.Create())
                {
                    foreach (InstallOperation operation in update.Operations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        OperationSupportInfo support = OperationSupport.Describe(operation.Type);

                        if (support.Kind != OperationSupportKind.Supported)
                        {
                            if (!options.IgnoreUnsupportedOperations)
                            {
                                throw new PayloadExtractionException(
                                    partitionName,
                                    support.Reason ?? ("operation " + operation.Type + " cannot be applied"));
                            }
                            skippedAnything = true;
                            operationsDone++;
                            continue;
                        }

                        // ZERO and DISCARD need no write: SetLength already zero-filled the image.
                        if (operation.Type == InstallOperation.Types.Type.Zero ||
                            operation.Type == InstallOperation.Types.Type.Discard)
                        {
                            bytesWritten += ExtentWriter.TotalBytes(operation.DstExtents, payload.BlockSize);
                            operationsDone++;
                            Report(progress, partitionName, bytesWritten, expectedSize, operationsDone, operationsTotal);
                            continue;
                        }

                        byte[] raw = ReadOperationData(payload, source, operation, partitionName);
                        try
                        {
                            if (options.VerifyOperationHashes && operation.HasDataSha256Hash)
                            {
                                byte[] actual = sha.ComputeHash(raw, 0, (int)operation.DataLength);
                                if (!BytesEqual(actual, operation.DataSha256Hash.ToByteArray()))
                                {
                                    throw new PayloadExtractionException(
                                        partitionName,
                                        "operation " + operationsDone + " failed its data hash check");
                                }
                            }

                            try
                            {
                                OperationCodec.DecodeInto(
                                    operation.Type, raw, (int)operation.DataLength, decoded);
                            }
                            catch (PayloadExtractionException)
                            {
                                throw;
                            }
                            catch (Exception e)
                            {
                                throw new PayloadExtractionException(
                                    partitionName,
                                    "operation " + operationsDone + " (" + support.Codec + ") could not be decoded: " + e.Message,
                                    e);
                            }

                            long produced = decoded.Length;
                            ExtentWriter.Write(
                                output, operation.DstExtents, payload.BlockSize, decoded, copyBuffer);
                            bytesWritten += produced;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(raw);
                        }

                        operationsDone++;
                        Report(progress, partitionName, bytesWritten, expectedSize, operationsDone, operationsTotal);
                    }
                }

                output.Flush();

                if (options.VerifyImageHash && !skippedAnything)
                {
                    VerifyImage(payload, partitionName, output, expectedSize, copyBuffer, cancellationToken);
                }

                return expectedSize;
            }
            finally
            {
                decoded.Dispose();
                ArrayPool<byte>.Shared.Return(copyBuffer);
            }
        }

        static byte[] ReadOperationData(
            PayloadFile payload, Stream source, InstallOperation operation, string partitionName)
        {
            long length = (long)operation.DataLength;
            if (length < 0 || length > int.MaxValue)
            {
                throw new PayloadExtractionException(
                    partitionName, "an operation declares an unusable data length of " + length);
            }

            long offset = payload.DataStart + (long)operation.DataOffset;
            if (offset < payload.DataStart || offset + length > source.Length)
            {
                throw new PayloadExtractionException(
                    partitionName, "an operation points outside the payload; the file looks truncated");
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(length, 1));
            try
            {
                source.Position = offset;
                int read = 0;
                while (read < length)
                {
                    int got = source.Read(buffer, read, (int)(length - read));
                    if (got <= 0)
                        throw new PayloadExtractionException(partitionName, "the payload ended while reading an operation");
                    read += got;
                }
                return buffer;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        static void VerifyImage(
            PayloadFile payload,
            string partitionName,
            Stream output,
            long expectedSize,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            PartitionUpdate update = payload.FindUpdate(partitionName);
            PartitionInfo expected = update.NewPartitionInfo;
            if (expected == null)
                return;

            if (expected.HasSize && output.Length != (long)expected.Size)
            {
                throw new PayloadExtractionException(
                    partitionName,
                    "image is " + output.Length + " bytes but the manifest says " + expected.Size);
            }

            if (!expected.HasHash || !output.CanRead)
                return;

            output.Position = 0;
            using (SHA256 sha = SHA256.Create())
            {
                long left = expectedSize;
                while (left > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, left);
                    int got = output.Read(buffer, 0, want);
                    if (got <= 0)
                        throw new PayloadExtractionException(partitionName, "could not read the image back to verify it");
                    sha.TransformBlock(buffer, 0, got, null, 0);
                    left -= got;
                }
                sha.TransformFinalBlock(buffer, 0, 0);

                if (!BytesEqual(sha.Hash, expected.Hash.ToByteArray()))
                {
                    throw new PayloadExtractionException(
                        partitionName,
                        "the finished image does not match the SHA-256 in the manifest");
                }
            }
        }

        static void Report(
            IProgress<ExtractionProgress> progress,
            string partition,
            long bytesWritten,
            long totalBytes,
            int operationsDone,
            int operationsTotal)
        {
            if (progress == null)
                return;

            progress.Report(new ExtractionProgress
            {
                Partition = partition,
                BytesWritten = bytesWritten,
                TotalBytes = totalBytes,
                OperationsDone = operationsDone,
                OperationsTotal = operationsTotal
            });
        }

        static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];
            return diff == 0;
        }
    }
}
