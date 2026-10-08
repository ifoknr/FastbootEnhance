using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FastbootEnhance.Core.Images
{
    /// <summary>
    /// A read-only, seekable view of the expanded image behind one or more sparse files,
    /// without writing it out. Several files are the parts of one image split for fastboot
    /// ("system.img_sparsechunk.0", ".1", ...): each covers the whole image and marks what the
    /// others hold as "don't care", so together they are laid over each other. Anything no
    /// part covers reads as zeros.
    /// </summary>
    public sealed class SparseStream : Stream
    {
        sealed class Segment
        {
            public long Start;
            public long Length;
            public int Part;
            public long DataOffset;
            public bool IsFill;
            public uint Fill;
        }

        readonly FileStream[] files;
        readonly List<Segment> segments;
        readonly long length;
        long position;

        public IReadOnlyList<SparseImage> Parts { get; }
        public int BlockSize { get; }

        SparseStream(IReadOnlyList<SparseImage> parts)
        {
            Parts = parts;
            BlockSize = parts[0].BlockSize;
            length = parts[0].OutputLength;
            files = parts.Select(p => new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess)).ToArray();

            segments = new List<Segment>();
            for (int i = 0; i < parts.Count; i++)
            {
                foreach (SparseChunk chunk in parts[i].Chunks)
                {
                    if (chunk.Type != SparseChunkType.Raw && chunk.Type != SparseChunkType.Fill)
                        continue;
                    segments.Add(new Segment
                    {
                        Start = chunk.OutputBlock * BlockSize,
                        Length = chunk.Blocks * BlockSize,
                        Part = i,
                        DataOffset = chunk.DataOffset,
                        IsFill = chunk.Type == SparseChunkType.Fill,
                        Fill = chunk.Value,
                    });
                }
            }

            segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 1; i < segments.Count; i++)
            {
                if (segments[i - 1].Start + segments[i - 1].Length > segments[i].Start)
                {
                    Dispose();
                    throw new InvalidDataException("the sparse parts overlap at byte " + segments[i].Start + "; they do not belong to one image");
                }
            }
        }

        /// <summary>Opens the parts of one image (a single file is the usual case).</summary>
        public static SparseStream Open(IEnumerable<string> paths)
        {
            List<SparseImage> parts = paths.Select(SparseImage.Open).ToList();
            if (parts.Count == 0)
                throw new ArgumentException("no sparse files given", nameof(paths));
            foreach (SparseImage part in parts.Skip(1))
            {
                if (part.BlockSize != parts[0].BlockSize || part.TotalBlocks != parts[0].TotalBlocks)
                {
                    throw new InvalidDataException(System.IO.Path.GetFileName(part.Path)
                        + " describes a different image size than " + System.IO.Path.GetFileName(parts[0].Path));
                }
            }
            return new SparseStream(parts);
        }

        public static SparseStream Open(string path)
        {
            return Open(new[] { path });
        }

        /// <summary>The covered regions in order, as (start, length) in the expanded image.</summary>
        public IEnumerable<KeyValuePair<long, long>> Regions =>
            segments.Select(s => new KeyValuePair<long, long>(s.Start, s.Length));

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get { return position; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length || count == 0)
                return 0;
            count = (int)Math.Min(count, length - position);

            int index = FindSegment(position);
            int done = 0;
            while (done < count)
            {
                long at = position + done;
                while (index < segments.Count && segments[index].Start + segments[index].Length <= at)
                    index++;

                int want = count - done;
                if (index >= segments.Count || segments[index].Start > at)
                {
                    // A hole: zeros up to the next covered region.
                    long next = index < segments.Count ? segments[index].Start : length;
                    int zeros = (int)Math.Min(want, next - at);
                    Array.Clear(buffer, offset + done, zeros);
                    done += zeros;
                    continue;
                }

                Segment segment = segments[index];
                long within = at - segment.Start;
                int take = (int)Math.Min(want, segment.Length - within);
                if (segment.IsFill)
                {
                    FillPattern(buffer, offset + done, take, segment.Fill, within);
                }
                else
                {
                    FileStream file = files[segment.Part];
                    file.Position = segment.DataOffset + within;
                    int read = 0;
                    while (read < take)
                    {
                        int got = file.Read(buffer, offset + done + read, take - read);
                        if (got <= 0)
                            throw new EndOfStreamException("sparse file ended inside a raw chunk");
                        read += got;
                    }
                }
                done += take;
            }

            position += done;
            return done;
        }

        static void FillPattern(byte[] buffer, int offset, int count, uint value, long phase)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] = (byte)(value >> (int)(((phase + i) & 3) * 8));
        }

        int FindSegment(long at)
        {
            int lo = 0, hi = segments.Count - 1, found = segments.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (segments[mid].Start + segments[mid].Length > at)
                {
                    found = mid;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return found;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
            Position = target;
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && files != null)
            {
                foreach (FileStream file in files)
                    file?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
