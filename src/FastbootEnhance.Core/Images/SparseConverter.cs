using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace FastbootEnhance.Core.Images
{
    /// <summary>What a sparse-to-raw conversion produced and whether its checksums held.</summary>
    public sealed class ExpandResult
    {
        public long Bytes { get; internal set; }

        /// <summary>CRC-32 of the whole expanded image.</summary>
        public uint Crc32 { get; internal set; }

        /// <summary>Checksums the image carried (header and CRC32 chunks) that were compared.</summary>
        public int ChecksumsChecked { get; internal set; }
    }

    /// <summary>What a conversion to sparse wrote.</summary>
    public sealed class SparseWriteResult
    {
        public IReadOnlyList<string> Files { get; internal set; }
        public long Blocks { get; internal set; }
        public int Chunks { get; internal set; }
        public long Bytes { get; internal set; }
    }

    /// <summary>
    /// simg2img, img2simg and simg2simg, natively: expands sparse images (one or several
    /// parts), turns raw images into sparse ones the way AOSP's img2simg does, and splits a
    /// sparse image into parts no larger than a given size the way simg2simg does.
    /// </summary>
    public static class SparseConverter
    {
        const int SparseHeaderSize = 28;
        const int ChunkHeaderSize = 12;
        const int CopyBuffer = 1 << 20;

        // ------------------------------------------------------------ sparse -> raw

        /// <summary>
        /// Expands one sparse image, or the parts of one, into a raw image. Checksums recorded
        /// in a single image (its header and any CRC32 chunks) are verified; a mismatch throws.
        /// </summary>
        public static ExpandResult ToRaw(IList<string> parts, string output, IProgress<long> progress,
            CancellationToken cancellation)
        {
            using (SparseStream source = SparseStream.Open(parts))
            using (FileStream destination = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer))
            {
                ExpandResult result = new ExpandResult();
                Crc32 crc = new Crc32();
                SparseImage single = source.Parts.Count == 1 ? source.Parts[0] : null;
                byte[] buffer = new byte[CopyBuffer];
                long total = source.Length;

                // Checksum checkpoints: the end of each CRC32 chunk's preceding data, and the end.
                List<KeyValuePair<long, uint>> checkpoints = new List<KeyValuePair<long, uint>>();
                if (single != null)
                {
                    foreach (SparseChunk chunk in single.Chunks.Where(c => c.Type == SparseChunkType.Crc32))
                        checkpoints.Add(new KeyValuePair<long, uint>(chunk.OutputBlock * single.BlockSize, chunk.Value));
                    if (single.ImageChecksum != 0)
                        checkpoints.Add(new KeyValuePair<long, uint>(total, single.ImageChecksum));
                }
                int nextCheck = 0;

                long done = 0;
                long lastReport = 0;
                while (done < total || nextCheck < checkpoints.Count)
                {
                    while (nextCheck < checkpoints.Count && checkpoints[nextCheck].Key == done)
                    {
                        if (crc.Value != checkpoints[nextCheck].Value)
                        {
                            throw new InvalidDataException("CRC-32 mismatch at byte " + done + ": the image says "
                                + checkpoints[nextCheck].Value.ToString("x8") + ", the data gives " + crc.Value.ToString("x8"));
                        }
                        result.ChecksumsChecked++;
                        nextCheck++;
                    }
                    if (done >= total)
                        break;

                    cancellation.ThrowIfCancellationRequested();
                    long limit = nextCheck < checkpoints.Count ? checkpoints[nextCheck].Key : total;
                    int want = (int)Math.Min(buffer.Length, limit - done);
                    source.Position = done;
                    int got = source.Read(buffer, 0, want);
                    if (got <= 0)
                        throw new EndOfStreamException("sparse image ended early");
                    destination.Write(buffer, 0, got);
                    crc.Append(buffer, 0, got);
                    done += got;

                    if (progress != null && done - lastReport >= (16 << 20))
                    {
                        lastReport = done;
                        progress.Report(done);
                    }
                }

                if (progress != null)
                    progress.Report(done);
                result.Bytes = done;
                result.Crc32 = crc.Value;
                return result;
            }
        }

        // ------------------------------------------------------------ -> sparse

        /// <summary>A chunk to write: data from the source file, or a fill pattern.</summary>
        sealed class PlanChunk
        {
            public bool IsFill;
            public long Block;
            public long Blocks;
            public long SourceOffset;
            public long SourceLength; // bytes of real data; the last block of a raw image may be short
            public uint Fill;

            public long WrittenSize(int blockSize)
            {
                return IsFill ? ChunkHeaderSize + 4 : ChunkHeaderSize + Blocks * blockSize;
            }
        }

        sealed class Plan
        {
            public int BlockSize;
            public long TotalBlocks;
            public string Source;
            public List<PlanChunk> Chunks = new List<PlanChunk>();
        }

        /// <summary>
        /// Scans a raw image block by block like AOSP's img2simg: a block made of one repeated
        /// 32-bit word becomes a fill chunk, anything else raw data. Neighbouring blocks of the
        /// same kind are merged.
        /// </summary>
        static Plan PlanRaw(string input, int blockSize, IProgress<long> progress, CancellationToken cancellation)
        {
            if (blockSize < 1024 || blockSize % 4 != 0)
                throw new ArgumentException("block size must be at least 1024 and a multiple of 4", nameof(blockSize));

            Plan plan = new Plan { BlockSize = blockSize, Source = input };
            long maxRawBlocks = (uint.MaxValue - ChunkHeaderSize) / blockSize;

            using (FileStream file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, FileOptions.SequentialScan))
            {
                long length = file.Length;
                plan.TotalBlocks = (length + blockSize - 1) / blockSize;
                byte[] block = new byte[blockSize];
                PlanChunk current = null;
                long offset = 0;
                long lastReport = 0;

                for (long index = 0; index < plan.TotalBlocks; index++)
                {
                    if ((index & 255) == 0)
                        cancellation.ThrowIfCancellationRequested();

                    int size = (int)Math.Min(blockSize, length - offset);
                    int read = 0;
                    while (read < size)
                    {
                        int got = file.Read(block, read, size - read);
                        if (got <= 0)
                            throw new EndOfStreamException("raw image shrank while it was being read");
                        read += got;
                    }

                    bool uniform = size == blockSize;
                    uint word = BitConverter.ToUInt32(block, 0);
                    if (uniform)
                    {
                        for (int i = 4; i < blockSize; i += 4)
                        {
                            if (BitConverter.ToUInt32(block, i) != word)
                            {
                                uniform = false;
                                break;
                            }
                        }
                    }

                    if (current != null && current.IsFill == uniform
                        && (uniform ? current.Fill == word : current.Blocks < maxRawBlocks))
                    {
                        current.Blocks++;
                        current.SourceLength += size;
                    }
                    else
                    {
                        current = new PlanChunk
                        {
                            IsFill = uniform,
                            Block = index,
                            Blocks = 1,
                            SourceOffset = offset,
                            SourceLength = size,
                            Fill = word,
                        };
                        plan.Chunks.Add(current);
                    }

                    offset += size;
                    if (progress != null && offset - lastReport >= (16 << 20))
                    {
                        lastReport = offset;
                        progress.Report(offset);
                    }
                }
            }
            return plan;
        }

        /// <summary>The chunks of an existing sparse image, for re-splitting it.</summary>
        static Plan PlanSparse(SparseImage image)
        {
            Plan plan = new Plan { BlockSize = image.BlockSize, TotalBlocks = image.TotalBlocks, Source = image.Path };
            foreach (SparseChunk chunk in image.Chunks)
            {
                if (chunk.Type == SparseChunkType.Raw)
                {
                    plan.Chunks.Add(new PlanChunk { Block = chunk.OutputBlock, Blocks = chunk.Blocks, SourceOffset = chunk.DataOffset, SourceLength = chunk.DataLength });
                }
                else if (chunk.Type == SparseChunkType.Fill)
                {
                    plan.Chunks.Add(new PlanChunk { IsFill = true, Block = chunk.OutputBlock, Blocks = chunk.Blocks, Fill = chunk.Value });
                }
            }
            return plan;
        }

        /// <summary>
        /// Writes a raw image (or re-writes a sparse one) as an Android sparse image. With
        /// <paramref name="maxPartBytes"/> the result is split, like simg2simg, into files
        /// named output.0, output.1, ... that are each at most that size and that fastboot
        /// (or simg2img given all of them) puts back together.
        /// </summary>
        public static SparseWriteResult ToSparse(string input, string output, int blockSize, long? maxPartBytes,
            IProgress<long> progress, CancellationToken cancellation)
        {
            Plan plan = SparseImage.IsSparse(input)
                ? PlanSparse(SparseImage.Open(input))
                : PlanRaw(input, blockSize, progress, cancellation);

            List<List<PlanChunk>> parts = maxPartBytes.HasValue ? SplitPlan(plan, maxPartBytes.Value) : new List<List<PlanChunk>> { plan.Chunks };

            List<string> files = new List<string>();
            long bytes = 0;
            int chunks = 0;
            using (FileStream source = new FileStream(plan.Source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, FileOptions.RandomAccess))
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    string path = maxPartBytes.HasValue ? output + "." + i : output;
                    int written;
                    bytes += WritePart(plan, parts[i], source, path, cancellation, out written);
                    chunks += written;
                    files.Add(path);
                }
            }

            return new SparseWriteResult { Files = files, Blocks = plan.TotalBlocks, Chunks = chunks, Bytes = bytes };
        }

        /// <summary>
        /// Packs chunks into parts of at most <paramref name="maxBytes"/>, splitting a raw
        /// chunk when at least an eighth of the part would otherwise go unused, as libsparse
        /// does. Each part keeps room for its header and the don't-care chunks around it.
        /// </summary>
        static List<List<PlanChunk>> SplitPlan(Plan plan, long maxBytes)
        {
            long overhead = SparseHeaderSize + 2 * ChunkHeaderSize + 4;
            long budget = maxBytes - overhead;
            if (budget < ChunkHeaderSize * 2 + plan.BlockSize)
                throw new ArgumentException("the part size is too small for even one block", nameof(maxBytes));

            List<List<PlanChunk>> parts = new List<List<PlanChunk>>();
            List<PlanChunk> currentPart = new List<PlanChunk>();
            long used = 0;
            long lastEnd = 0;
            Queue<PlanChunk> pending = new Queue<PlanChunk>(plan.Chunks);

            while (pending.Count > 0)
            {
                PlanChunk chunk = pending.Peek();
                long gap = chunk.Block > lastEnd ? ChunkHeaderSize : 0;
                long cost = gap + chunk.WrittenSize(plan.BlockSize);

                if (used + cost <= budget)
                {
                    currentPart.Add(pending.Dequeue());
                    used += cost;
                    lastEnd = chunk.Block + chunk.Blocks;
                    continue;
                }

                long room = budget - used - gap - ChunkHeaderSize;
                long fitBlocks = room / plan.BlockSize;
                if (!chunk.IsFill && room > budget / 8 && fitBlocks > 0)
                {
                    // Split the raw chunk so this part ends up nearly full.
                    pending.Dequeue();
                    PlanChunk head = new PlanChunk { Block = chunk.Block, Blocks = fitBlocks, SourceOffset = chunk.SourceOffset, SourceLength = fitBlocks * plan.BlockSize };
                    PlanChunk tail = new PlanChunk
                    {
                        Block = chunk.Block + fitBlocks,
                        Blocks = chunk.Blocks - fitBlocks,
                        SourceOffset = chunk.SourceOffset + fitBlocks * plan.BlockSize,
                        SourceLength = chunk.SourceLength - fitBlocks * plan.BlockSize,
                    };
                    currentPart.Add(head);
                    List<PlanChunk> rest = new List<PlanChunk> { tail };
                    rest.AddRange(pending);
                    pending = new Queue<PlanChunk>(rest);
                }
                else if (currentPart.Count == 0)
                {
                    // A single chunk larger than a part on its own: it must be split.
                    if (chunk.IsFill)
                        throw new InvalidOperationException("a fill chunk does not fit in a part");
                    fitBlocks = Math.Max(1, (budget - ChunkHeaderSize * 2) / plan.BlockSize);
                    pending.Dequeue();
                    currentPart.Add(new PlanChunk { Block = chunk.Block, Blocks = fitBlocks, SourceOffset = chunk.SourceOffset, SourceLength = fitBlocks * plan.BlockSize });
                    List<PlanChunk> rest = new List<PlanChunk>
                    {
                        new PlanChunk
                        {
                            Block = chunk.Block + fitBlocks,
                            Blocks = chunk.Blocks - fitBlocks,
                            SourceOffset = chunk.SourceOffset + fitBlocks * plan.BlockSize,
                            SourceLength = chunk.SourceLength - fitBlocks * plan.BlockSize,
                        },
                    };
                    rest.AddRange(pending);
                    pending = new Queue<PlanChunk>(rest);
                }

                parts.Add(currentPart);
                currentPart = new List<PlanChunk>();
                used = 0;
                lastEnd = 0;
            }

            if (currentPart.Count > 0 || parts.Count == 0)
                parts.Add(currentPart);
            return parts;
        }

        /// <summary>Writes one sparse file; gaps between chunks become don't-care chunks.</summary>
        static long WritePart(Plan plan, List<PlanChunk> chunks, FileStream source, string path,
            CancellationToken cancellation, out int chunkCount)
        {
            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer))
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                file.Position = SparseHeaderSize;
                byte[] buffer = new byte[CopyBuffer];
                int count = 0;
                long cursor = 0;

                foreach (PlanChunk chunk in chunks)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (chunk.Block > cursor)
                    {
                        WriteChunkHeader(writer, SparseChunkType.DontCare, chunk.Block - cursor, ChunkHeaderSize);
                        count++;
                    }

                    if (chunk.IsFill)
                    {
                        WriteChunkHeader(writer, SparseChunkType.Fill, chunk.Blocks, ChunkHeaderSize + 4);
                        writer.Write(chunk.Fill);
                    }
                    else
                    {
                        long dataBytes = chunk.Blocks * plan.BlockSize;
                        WriteChunkHeader(writer, SparseChunkType.Raw, chunk.Blocks, ChunkHeaderSize + dataBytes);
                        writer.Flush();
                        source.Position = chunk.SourceOffset;
                        long left = chunk.SourceLength;
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buffer.Length, left);
                            int got = source.Read(buffer, 0, want);
                            if (got <= 0)
                                throw new EndOfStreamException("source ended inside a raw chunk");
                            file.Write(buffer, 0, got);
                            left -= got;
                        }
                        // The last block of a raw image may be short; pad it with zeros.
                        long padding = dataBytes - chunk.SourceLength;
                        if (padding > 0)
                            file.Write(new byte[padding], 0, (int)padding);
                    }
                    count++;
                    cursor = chunk.Block + chunk.Blocks;
                }

                if (cursor < plan.TotalBlocks)
                {
                    WriteChunkHeader(writer, SparseChunkType.DontCare, plan.TotalBlocks - cursor, ChunkHeaderSize);
                    count++;
                }

                writer.Flush();
                long length = file.Length;
                file.Position = 0;
                writer.Write(SparseImage.Magic);
                writer.Write((ushort)1);
                writer.Write((ushort)0);
                writer.Write((ushort)SparseHeaderSize);
                writer.Write((ushort)ChunkHeaderSize);
                writer.Write((uint)plan.BlockSize);
                writer.Write((uint)plan.TotalBlocks);
                writer.Write((uint)count);
                writer.Write(0u);
                writer.Flush();

                chunkCount = count;
                return length;
            }
        }

        static void WriteChunkHeader(BinaryWriter writer, SparseChunkType type, long blocks, long totalSize)
        {
            if (blocks > uint.MaxValue || totalSize > uint.MaxValue)
                throw new InvalidOperationException("chunk too large for the sparse format");
            writer.Write((ushort)type);
            writer.Write((ushort)0);
            writer.Write((uint)blocks);
            writer.Write((uint)totalSize);
        }

        // ------------------------------------------------------------ split parts

        static readonly Regex PartName = new Regex(@"^(?<stem>.*?)(?<sep>[._]sparsechunk)?\.(?<n>\d+)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Given one part of a split image ("system.img_sparsechunk.3", "super.img.0"), finds
        /// all its siblings in the same folder, in order. A file that is not such a part comes
        /// back on its own.
        /// </summary>
        public static IList<string> FindParts(string path)
        {
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            string name = System.IO.Path.GetFileName(path);
            Match match = PartName.Match(name);
            if (!match.Success)
                return new[] { path };

            string prefix = match.Groups["stem"].Value + match.Groups["sep"].Value + ".";
            List<KeyValuePair<long, string>> found = new List<KeyValuePair<long, string>>();
            foreach (string candidate in Directory.EnumerateFiles(folder))
            {
                string candidateName = System.IO.Path.GetFileName(candidate);
                if (!candidateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string number = candidateName.Substring(prefix.Length);
                long n;
                if (number.Length == 0 || !number.All(char.IsDigit) || !long.TryParse(number, out n))
                    continue;
                if (!SparseImage.IsSparse(candidate))
                    continue;
                found.Add(new KeyValuePair<long, string>(n, candidate));
            }

            if (found.Count < 2)
                return new[] { path };
            return found.OrderBy(f => f.Key).Select(f => f.Value).ToList();
        }
    }
}
