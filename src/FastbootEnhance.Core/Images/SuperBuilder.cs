using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FastbootEnhance.Core.Images
{
    /// <summary>Where one partition's data lands in a super image being built.</summary>
    public sealed class SuperPlacement
    {
        internal SuperPlacement(string name, long offset, long dataLength, long allocated, Stream data)
        {
            Name = name;
            Offset = offset;
            DataLength = dataLength;
            Allocated = allocated;
            Data = data;
        }

        public string Name { get; }

        /// <summary>Byte offset of the partition's single extent in the image.</summary>
        public long Offset { get; }

        /// <summary>Bytes of image data written there.</summary>
        public long DataLength { get; }

        /// <summary>The partition size: the data rounded up to whole 4096-byte blocks.</summary>
        public long Allocated { get; }

        internal Stream Data { get; }
    }

    /// <summary>The finished layout: metadata bytes and where every partition goes.</summary>
    public sealed class SuperLayout
    {
        internal SuperLayout()
        {
        }

        public long TotalSize { get; internal set; }

        /// <summary>Everything before the first partition: reserved bytes, geometry and metadata.</summary>
        public byte[] Metadata { get; internal set; }

        /// <summary>The first byte partitions can use (the metadata area rounded up to the alignment).</summary>
        public long FirstUsable { get; internal set; }

        /// <summary>Where the last partition ends, rounded up to the alignment.</summary>
        public long UsedEnd { get; internal set; }

        public IReadOnlyList<SuperPlacement> Placements { get; internal set; }

        /// <summary>Bytes of partition data the image will hold.</summary>
        public long DataBytes => Placements.Sum(p => p.DataLength);
    }

    /// <summary>
    /// Writes a super image with a valid liblp metadata table from partition images, laid out
    /// the way AOSP's lpmake does on a host: geometry and metadata first, then each partition
    /// as one extent at the next aligned sector, in the order they were added. Empty partitions
    /// (the inactive slot) get no extents.
    /// </summary>
    public sealed class SuperBuilder
    {
        public const int BlockSize = 4096;
        const int ReservedBytes = 4096;
        const int GeometrySize = 4096;

        sealed class Entry
        {
            public string Name;
            public string Group;
            public Stream Data;
            public long Size;
            public bool Readonly;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly List<KeyValuePair<string, ulong>> groups = new List<KeyValuePair<string, ulong>>();

        public uint MetadataMaxSize { get; set; } = 65536;
        public uint SlotCount { get; set; } = 2;
        public uint Alignment { get; set; } = 1 << 20;

        /// <summary>Header 10.2 (256 bytes, with flags) when true; 10.0 (128 bytes) when false.</summary>
        public bool ExpandedHeader { get; set; } = true;

        public uint HeaderFlags { get; set; }

        public void AddGroup(string name, ulong maximumSize)
        {
            int at = groups.FindIndex(g => g.Key == name);
            if (at >= 0)
                groups[at] = new KeyValuePair<string, ulong>(name, maximumSize);
            else
                groups.Add(new KeyValuePair<string, ulong>(name, maximumSize));
        }

        /// <summary>Adds a partition; an empty one (no data) gets no extents, like an inactive slot.</summary>
        public void AddPartition(string name, string group, Stream data, bool readOnly = true)
        {
            entries.Add(new Entry { Name = name, Group = group, Data = data, Size = data == null ? 0 : data.Length, Readonly = readOnly });
        }

        /// <summary>The metadata area, reserved bytes and both geometry copies included.</summary>
        public static long MetadataAreaSize(uint metadataMaxSize, uint slotCount)
        {
            return ReservedBytes + 2L * (GeometrySize + (long)metadataMaxSize * slotCount);
        }

        /// <summary>Works out the layout and serialises the metadata, without writing anything.</summary>
        public SuperLayout Plan(long totalSize)
        {
            const int sector = SuperImage.SectorSize;
            if (Alignment == 0 || Alignment % BlockSize != 0)
                throw new ArgumentException("the alignment must be a multiple of " + BlockSize);
            long metadataEnd = MetadataAreaSize(MetadataMaxSize, SlotCount);
            long firstUsable = Align(metadataEnd, Alignment);
            long cursor = firstUsable;

            List<string> groupNames = new List<string> { "default" };
            groupNames.AddRange(groups.Select(g => g.Key).Where(g => g != "default"));

            List<byte[]> partitionRows = new List<byte[]>();
            List<byte[]> extentRows = new List<byte[]>();
            List<SuperPlacement> placements = new List<SuperPlacement>();
            foreach (Entry entry in entries)
            {
                // Like lpmake: a partition occupies whole logical blocks.
                long size = Align(entry.Size, BlockSize);
                int firstExtent = extentRows.Count;
                if (size > 0)
                {
                    long at = cursor;
                    extentRows.Add(Pack(w =>
                    {
                        w.Write((ulong)(size / sector));
                        w.Write(SuperImage.TargetLinear);
                        w.Write((ulong)(at / sector));
                        w.Write(0u);
                    }));
                    placements.Add(new SuperPlacement(entry.Name, cursor, entry.Size, size, entry.Data));
                    cursor = Align(cursor + size, Alignment);
                }
                int groupIndex = groupNames.IndexOf(entry.Group);
                if (groupIndex < 0)
                    throw new ArgumentException("unknown group " + entry.Group);
                int count = extentRows.Count - firstExtent;
                partitionRows.Add(Pack(w =>
                {
                    w.Write(Name(entry.Name, 36));
                    w.Write(entry.Readonly ? 1u : 0u);
                    w.Write((uint)firstExtent);
                    w.Write((uint)count);
                    w.Write((uint)groupIndex);
                }));
            }
            long lastEnd = placements.Count == 0 ? firstUsable : placements.Max(p => p.Offset + p.Allocated);
            if (lastEnd > totalSize)
                throw new ArgumentException("the partitions do not fit in " + totalSize + " bytes");

            List<byte[]> groupRows = groupNames.Select(g => Pack(w =>
            {
                w.Write(Name(g, 36));
                w.Write(0u);
                w.Write(g == "default" ? 0UL : groups.First(x => x.Key == g).Value);
            })).ToList();

            byte[] blockDevice = Pack(w =>
            {
                w.Write((ulong)(firstUsable / sector));
                w.Write(Alignment);
                w.Write(0u);
                w.Write((ulong)totalSize);
                w.Write(Name("super", 36));
                w.Write(0u);
            });

            // Tables, in the order liblp writes them.
            byte[] partitionsTable = partitionRows.SelectMany(r => r).ToArray();
            byte[] extentsTable = extentRows.SelectMany(r => r).ToArray();
            byte[] groupsTable = groupRows.SelectMany(r => r).ToArray();
            byte[] tables = partitionsTable.Concat(extentsTable).Concat(groupsTable).Concat(blockDevice).ToArray();

            int headerSize = ExpandedHeader ? 256 : 128;
            byte[] header = new byte[headerSize];
            using (BinaryWriter w = new BinaryWriter(new MemoryStream(header)))
            {
                w.Write(0x414C5030u);
                w.Write((ushort)10);
                w.Write((ushort)(ExpandedHeader ? 2 : 0));
                w.Write((uint)headerSize);
                w.Write(new byte[32]);
                w.Write((uint)tables.Length);
                w.Write(Sha256(tables));
                Descriptor(w, 0, partitionRows.Count, 52);
                Descriptor(w, partitionsTable.Length, extentRows.Count, 24);
                Descriptor(w, partitionsTable.Length + extentsTable.Length, groupRows.Count, 48);
                Descriptor(w, partitionsTable.Length + extentsTable.Length + groupsTable.Length, 1, 64);
                if (ExpandedHeader)
                    w.Write(HeaderFlags);
            }
            Array.Copy(Sha256(header), 0, header, 12, 32);
            if (headerSize + tables.Length > MetadataMaxSize)
                throw new InvalidOperationException("metadata does not fit in MetadataMaxSize");

            byte[] geometry = new byte[GeometrySize];
            using (BinaryWriter w = new BinaryWriter(new MemoryStream(geometry)))
            {
                w.Write(0x616C4467u);
                w.Write(52u);
                w.Write(new byte[32]);
                w.Write(MetadataMaxSize);
                w.Write(SlotCount);
                w.Write((uint)BlockSize);
            }
            Array.Copy(Sha256(geometry.Take(52).ToArray()), 0, geometry, 8, 32);

            // Reserved zeros, two geometry copies, then every slot twice (primary, then backup),
            // each padded to the maximum metadata size.
            byte[] metadata = new byte[metadataEnd];
            Array.Copy(geometry, 0, metadata, ReservedBytes, GeometrySize);
            Array.Copy(geometry, 0, metadata, ReservedBytes + GeometrySize, GeometrySize);
            for (int copy = 0; copy < 2 * SlotCount; copy++)
            {
                long at = ReservedBytes + 2L * GeometrySize + (long)MetadataMaxSize * copy;
                Array.Copy(header, 0, metadata, at, headerSize);
                Array.Copy(tables, 0, metadata, at + headerSize, tables.Length);
            }

            return new SuperLayout
            {
                TotalSize = totalSize,
                Metadata = metadata,
                FirstUsable = firstUsable,
                UsedEnd = placements.Count == 0 ? firstUsable : Align(lastEnd, Alignment),
                Placements = placements,
            };
        }

        /// <summary>Writes a raw image to a seekable stream.</summary>
        public void Write(Stream output, long totalSize)
        {
            SuperLayout layout = Plan(totalSize);
            output.SetLength(totalSize);
            output.Position = 0;
            output.Write(layout.Metadata, 0, layout.Metadata.Length);

            byte[] buffer = new byte[1 << 20];
            foreach (SuperPlacement place in layout.Placements)
            {
                output.Position = place.Offset;
                place.Data.Position = 0;
                int got;
                while ((got = place.Data.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, got);
            }
        }

        /// <summary>
        /// Writes the image to <paramref name="path"/>, raw or as an Android sparse image (what
        /// lpmake --sparse makes and fastboot flashes). Free space between partitions is left
        /// out of a sparse image. <paramref name="progress"/> counts bytes of partition data.
        /// </summary>
        public SuperLayout WriteFile(string path, long totalSize, bool sparse, IProgress<long> progress,
            CancellationToken cancellation)
        {
            SuperLayout layout = Plan(totalSize);
            if (totalSize % BlockSize != 0)
                throw new ArgumentException("the super size must be a multiple of " + BlockSize);

            byte[] block = new byte[BlockSize];
            long done = 0;
            long lastReport = 0;
            long blocks = 0;

            using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (SparseWriter writer = sparse ? new SparseWriter(file, BlockSize, totalSize / BlockSize) : null)
            {
                if (writer != null)
                {
                    for (int at = 0; at < layout.Metadata.Length; at += BlockSize)
                        writer.Write(layout.Metadata, at);
                }
                else
                {
                    file.SetLength(totalSize);
                    file.Write(layout.Metadata, 0, layout.Metadata.Length);
                }

                long position = layout.Metadata.Length;
                foreach (SuperPlacement place in layout.Placements.OrderBy(p => p.Offset))
                {
                    if (writer != null)
                        writer.Skip((place.Offset - position) / BlockSize);
                    else
                        file.Position = place.Offset;

                    place.Data.Position = 0;
                    long left = place.DataLength;
                    while (left > 0)
                    {
                        if ((blocks++ & 1023) == 0)
                            cancellation.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(BlockSize, left);
                        int got = 0;
                        while (got < want)
                        {
                            int n = place.Data.Read(block, got, want - got);
                            if (n <= 0)
                                throw new EndOfStreamException("the image of " + place.Name + " ended early");
                            got += n;
                        }
                        // The last block of an image that is not a whole number of blocks.
                        if (got < BlockSize)
                            Array.Clear(block, got, BlockSize - got);

                        if (writer != null)
                            writer.Write(block, 0);
                        else
                            file.Write(block, 0, BlockSize);

                        left -= got;
                        done += got;
                        if (progress != null && done - lastReport >= (16 << 20))
                        {
                            lastReport = done;
                            progress.Report(done);
                        }
                    }
                    position = place.Offset + place.Allocated;
                }

                if (writer != null)
                    writer.Finish();
            }

            if (progress != null)
                progress.Report(done);
            return layout;
        }

        static void Descriptor(BinaryWriter w, int offset, int count, int size)
        {
            w.Write((uint)offset);
            w.Write((uint)count);
            w.Write((uint)size);
        }

        static byte[] Name(string name, int length)
        {
            byte[] bytes = new byte[length];
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            Array.Copy(ascii, bytes, Math.Min(ascii.Length, length));
            return bytes;
        }

        static byte[] Pack(Action<BinaryWriter> write)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(memory))
            {
                write(w);
                w.Flush();
                return memory.ToArray();
            }
        }

        static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(data);
        }

        internal static long Align(long value, long alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }
    }
}
