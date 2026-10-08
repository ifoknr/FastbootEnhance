using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace FastbootEnhance.Core.Images
{
    /// <summary>One file of a partition cut into pieces, and where it goes in that partition.</summary>
    public sealed class RawProgramPiece
    {
        internal RawProgramPiece(string path, long offset, long length, long fileOffset, bool sparse, long startSector)
        {
            Path = path;
            Offset = offset;
            Length = length;
            FileOffset = fileOffset;
            Sparse = sparse;
            StartSector = startSector;
        }

        public string Path { get; }

        /// <summary>Byte offset of the piece in the partition.</summary>
        public long Offset { get; internal set; }

        /// <summary>Bytes of the piece written there.</summary>
        public long Length { get; }

        /// <summary>Where in the file the data starts (file_sector_offset).</summary>
        public long FileOffset { get; }

        /// <summary>True when the piece is itself an Android sparse image.</summary>
        public bool Sparse { get; }

        internal long StartSector { get; }
    }

    /// <summary>
    /// A partition shipped as pieces in a Qualcomm flash package (QFIL / QPST, the
    /// rawprogram*.xml files), like super_1.img ... super_17.img: each piece is written at its
    /// own sector of the partition and what lies between is left alone. Read together they
    /// are the partition's image; gaps read as zeros.
    /// </summary>
    public sealed class RawProgramImage
    {
        const long MaxXmlBytes = 32L << 20;
        const uint LpGeometryMagic = 0x616C4467;

        RawProgramImage()
        {
        }

        /// <summary>The partition the pieces belong to, e.g. "super".</summary>
        public string Label { get; private set; }

        public int SectorSize { get; private set; }

        /// <summary>The XML file that lists the pieces.</summary>
        public string Xml { get; private set; }

        public IReadOnlyList<RawProgramPiece> Pieces { get; private set; }

        /// <summary>The image size: the end of the last piece, or more when the content says so (super).</summary>
        public long Length { get; private set; }

        public long DataBytes => Pieces.Sum(p => p.Length);

        public IList<string> Files => Pieces.Select(p => p.Path).ToList();

        sealed class Entry
        {
            public string File;
            public string Label;
            public long Start;
            public long Sectors;
            public long FileSectorOffset;
            public int SectorSize;
            public string Lun;
            public bool Sparse;
            public string Xml;
        }

        /// <summary>
        /// Looks for a rawprogram XML next to <paramref name="anyPiece"/> that lists it as one of
        /// several pieces of a partition, and returns all the pieces. Null when the file is not
        /// such a piece (a normal image).
        /// </summary>
        public static RawProgramImage TryFind(string anyPiece)
        {
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(anyPiece));
            string name = System.IO.Path.GetFileName(anyPiece);
            List<Entry> entries = ReadFolder(folder);

            Entry mine = entries.FirstOrDefault(e => string.Equals(e.File, name, StringComparison.OrdinalIgnoreCase));
            if (mine == null)
                return null;

            List<Entry> partition = entries.Where(e => e.Label == mine.Label && e.Lun == mine.Lun && e.Xml == mine.Xml).ToList();
            List<Entry> withFiles = partition
                .Where(e => e.File.Length > 0 && File.Exists(System.IO.Path.Combine(folder, e.File)))
                .GroupBy(e => e.File.ToLowerInvariant() + "@" + e.Start)
                .Select(g => g.First())
                .OrderBy(e => e.Start)
                .ToList();
            if (withFiles.Count < 2)
                return null;

            int sectorSize = mine.SectorSize;
            // The partition starts at its own entry when the XML has one, else at the first piece.
            long baseSector = partition.Min(e => e.Start);

            List<RawProgramPiece> pieces = new List<RawProgramPiece>();
            foreach (Entry entry in withFiles)
            {
                string path = System.IO.Path.Combine(folder, entry.File);
                long fileOffset = entry.FileSectorOffset * sectorSize;
                long length;
                if (entry.Sparse)
                {
                    length = SparseImage.Open(path).OutputLength;
                }
                else
                {
                    length = new FileInfo(path).Length - fileOffset;
                    if (entry.Sectors > 0)
                        length = Math.Min(length, entry.Sectors * sectorSize);
                }
                if (length <= 0)
                    continue;
                pieces.Add(new RawProgramPiece(path, (entry.Start - baseSector) * sectorSize, length, fileOffset, entry.Sparse, entry.Start));
            }

            if (pieces.Count < 2)
                return null;

            RawProgramImage image = new RawProgramImage
            {
                Label = mine.Label,
                SectorSize = sectorSize,
                Xml = mine.Xml,
                Pieces = pieces,
            };
            image.Length = pieces.Max(p => p.Offset + p.Length);
            image.AlignSuper();
            return image;
        }

        /// <summary>
        /// Tools that drop all-zero data leave out super's first 4096 reserved bytes, so the
        /// first piece starts with the geometry block. Shift everything so it lands at 4096, and
        /// take the image size from super's own metadata.
        /// </summary>
        void AlignSuper()
        {
            RawProgramPiece first = Pieces[0];
            if (first.Offset == 0 && !first.Sparse && ReadMagic(first, 0) == LpGeometryMagic)
            {
                foreach (RawProgramPiece piece in Pieces)
                    piece.Offset += 4096;
                Length += 4096;
            }

            try
            {
                using (Stream stream = Open())
                {
                    if (!SuperImage.IsSuper(stream))
                        return;
                    SuperImage super = SuperImage.Read(stream);
                    if (super.BlockDevices.Count > 0 && (long)super.BlockDevices[0].Size > Length)
                        Length = (long)super.BlockDevices[0].Size;
                }
            }
            catch (InvalidDataException)
            {
                // Not readable as super: the pieces still make an image, just of their own size.
            }
        }

        static uint ReadMagic(RawProgramPiece piece, long at)
        {
            using (FileStream file = new FileStream(piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < piece.FileOffset + at + 4)
                    return 0;
                file.Position = piece.FileOffset + at;
                byte[] magic = new byte[4];
                return StreamUtil.TryReadExactly(file, magic) ? BitConverter.ToUInt32(magic, 0) : 0;
            }
        }

        static List<Entry> ReadFolder(string folder)
        {
            List<Entry> entries = new List<Entry>();
            foreach (string xml in Directory.GetFiles(folder, "*.xml"))
            {
                try
                {
                    if (new FileInfo(xml).Length > MaxXmlBytes)
                        continue;
                    XDocument document = XDocument.Load(xml);
                    foreach (XElement program in document.Descendants().Where(e => e.Name.LocalName.Equals("program", StringComparison.OrdinalIgnoreCase)))
                    {
                        Entry entry = Parse(program, xml);
                        if (entry != null)
                            entries.Add(entry);
                    }
                }
                catch (Exception e) when (e is System.Xml.XmlException || e is IOException || e is UnauthorizedAccessException)
                {
                    // Not a rawprogram file, or unreadable; other XML files may still be.
                }
            }
            return entries;
        }

        static string Attribute(XElement element, string name)
        {
            XAttribute attribute = element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            return attribute == null ? null : attribute.Value.Trim();
        }

        static long Number(string text, long fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;
            text = text.TrimEnd('.');
            long value;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : fallback;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        static Entry Parse(XElement program, string xml)
        {
            string label = Attribute(program, "label");
            // Entries placed from the end of the disk ("NUM_DISK_SECTORS-5.") cannot be located here.
            long start = Number(Attribute(program, "start_sector"), -1);
            if (string.IsNullOrEmpty(label) || start < 0)
                return null;
            return new Entry
            {
                File = Attribute(program, "filename") ?? "",
                Label = label,
                Start = start,
                Sectors = Number(Attribute(program, "num_partition_sectors"), 0),
                FileSectorOffset = Number(Attribute(program, "file_sector_offset"), 0),
                SectorSize = (int)Number(Attribute(program, "SECTOR_SIZE_IN_BYTES"), 512),
                Lun = Attribute(program, "physical_partition_number") ?? "0",
                Sparse = string.Equals(Attribute(program, "sparse"), "true", StringComparison.OrdinalIgnoreCase),
                Xml = xml,
            };
        }

        /// <summary>The whole image as one read-only stream.</summary>
        public Stream Open()
        {
            return new PiecedStream(this);
        }

        /// <summary>Writes the image as one raw file; gaps become zeros.</summary>
        public long WriteRaw(string output, IProgress<long> progress, CancellationToken cancellation)
        {
            byte[] buffer = new byte[1 << 20];
            long done = 0;
            long lastReport = 0;
            using (FileStream file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                file.SetLength(Length);
                foreach (RawProgramPiece piece in Pieces)
                {
                    using (Stream source = OpenPiece(piece))
                    {
                        file.Position = piece.Offset;
                        long left = piece.Length;
                        while (left > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int got = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (got <= 0)
                                throw new EndOfStreamException(System.IO.Path.GetFileName(piece.Path) + " ended early");
                            file.Write(buffer, 0, got);
                            left -= got;
                            done += got;
                            if (progress != null && done - lastReport >= (16 << 20))
                            {
                                lastReport = done;
                                progress.Report(done);
                            }
                        }
                    }
                }
            }
            if (progress != null)
                progress.Report(done);
            return Length;
        }

        /// <summary>
        /// Writes the image as one Android sparse file, what fastboot flashes: the pieces as data
        /// (zero blocks as fills), the gaps between them left out, as the flash tool leaves them.
        /// </summary>
        public long WriteSparse(string output, IProgress<long> progress, CancellationToken cancellation)
        {
            const int block = 4096;
            if (Length % block != 0 || Pieces.Any(p => p.Offset % block != 0))
                throw new InvalidDataException("the pieces are not aligned to 4096-byte blocks, so they cannot be written as a sparse image");

            byte[] buffer = new byte[block];
            long done = 0;
            long lastReport = 0;
            long blocks = 0;
            using (FileStream file = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (SparseWriter writer = new SparseWriter(file, block, Length / block))
            {
                long position = 0;
                foreach (RawProgramPiece piece in Pieces)
                {
                    if (piece.Offset < position)
                        throw new InvalidDataException(System.IO.Path.GetFileName(piece.Path) + " overlaps the piece before it");
                    writer.Skip((piece.Offset - position) / block);
                    using (Stream source = OpenPiece(piece))
                    {
                        long left = piece.Length;
                        while (left > 0)
                        {
                            if ((blocks++ & 1023) == 0)
                                cancellation.ThrowIfCancellationRequested();
                            int want = (int)Math.Min(block, left);
                            int got = 0;
                            while (got < want)
                            {
                                int n = source.Read(buffer, got, want - got);
                                if (n <= 0)
                                    throw new EndOfStreamException(System.IO.Path.GetFileName(piece.Path) + " ended early");
                                got += n;
                            }
                            if (got < block)
                                Array.Clear(buffer, got, block - got);
                            writer.Write(buffer, 0);
                            left -= got;
                            done += got;
                            if (progress != null && done - lastReport >= (16 << 20))
                            {
                                lastReport = done;
                                progress.Report(done);
                            }
                        }
                    }
                    position = piece.Offset + SuperBuilder.Align(piece.Length, block);
                }
                writer.Finish();
                if (progress != null)
                    progress.Report(done);
                return file.Length;
            }
        }

        /// <summary>A piece's data from its start (file_sector_offset applied).</summary>
        internal static Stream OpenPiece(RawProgramPiece piece)
        {
            if (piece.Sparse)
                return SparseStream.Open(piece.Path);
            FileStream file = new FileStream(piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            file.Position = piece.FileOffset;
            return file;
        }

        /// <summary>Reads the pieces as one image; gaps read as zeros.</summary>
        sealed class PiecedStream : Stream
        {
            readonly RawProgramImage image;
            readonly Stream[] open;
            long position;

            public PiecedStream(RawProgramImage image)
            {
                this.image = image;
                open = new Stream[image.Pieces.Count];
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => image.Length;

            public override long Position
            {
                get => position;
                set => position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (position >= image.Length || count <= 0)
                    return 0;
                count = (int)Math.Min(count, image.Length - position);

                IReadOnlyList<RawProgramPiece> pieces = image.Pieces;
                for (int i = 0; i < pieces.Count; i++)
                {
                    RawProgramPiece piece = pieces[i];
                    if (position < piece.Offset)
                    {
                        // A gap before this piece.
                        int zeros = (int)Math.Min(count, piece.Offset - position);
                        Array.Clear(buffer, offset, zeros);
                        position += zeros;
                        return zeros;
                    }
                    if (position < piece.Offset + piece.Length)
                    {
                        Stream stream = open[i] ?? (open[i] = RawProgramImage.OpenPiece(piece));
                        long inside = position - piece.Offset;
                        stream.Position = (piece.Sparse ? 0 : piece.FileOffset) + inside;
                        int want = (int)Math.Min(count, piece.Length - inside);
                        int got = stream.Read(buffer, offset, want);
                        if (got <= 0)
                            throw new EndOfStreamException(System.IO.Path.GetFileName(piece.Path) + " ended early");
                        position += got;
                        return got;
                    }
                }

                // After the last piece.
                Array.Clear(buffer, offset, count);
                position += count;
                return count;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : image.Length + offset;
                return position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    foreach (Stream stream in open)
                        stream?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
