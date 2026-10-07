using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FastbootEnhance.Core.Images
{
    /// <summary>
    /// Writes a raw super image with a valid liblp metadata table (like a minimal lpmake),
    /// from a list of partition images. Used to make test and sample images.
    /// </summary>
    public sealed class SuperBuilder
    {
        sealed class Entry
        {
            public string Name;
            public string Group;
            public Stream Data;
            public long Size;
            public bool Readonly;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly Dictionary<string, ulong> groups = new Dictionary<string, ulong>();

        public uint MetadataMaxSize { get; set; } = 65536;
        public uint SlotCount { get; set; } = 2;
        public uint Alignment { get; set; } = 1 << 20;

        /// <summary>Header 10.2 (256 bytes, with flags) when true; 10.0 (128 bytes) when false.</summary>
        public bool ExpandedHeader { get; set; } = true;

        public uint HeaderFlags { get; set; }

        public void AddGroup(string name, ulong maximumSize)
        {
            groups[name] = maximumSize;
        }

        /// <summary>Adds a partition; an empty one (no data) gets no extents, like an inactive slot.</summary>
        public void AddPartition(string name, string group, Stream data, bool readOnly = true)
        {
            entries.Add(new Entry { Name = name, Group = group, Data = data, Size = data == null ? 0 : data.Length, Readonly = readOnly });
        }

        public void Write(Stream output, long totalSize)
        {
            const int sector = SuperImage.SectorSize;
            long metadataEnd = 4096 + 4096 * 2 + 2L * SlotCount * MetadataMaxSize;
            long cursor = Align(metadataEnd, Alignment);

            List<string> groupNames = new List<string> { "default" };
            groupNames.AddRange(groups.Keys.Where(g => g != "default"));

            // Layout: each partition, aligned, one extent.
            List<byte[]> partitionRows = new List<byte[]>();
            List<byte[]> extentRows = new List<byte[]>();
            List<KeyValuePair<long, Entry>> placement = new List<KeyValuePair<long, Entry>>();
            foreach (Entry entry in entries)
            {
                // Like lpmake: a partition occupies whole logical blocks (4096 bytes).
                long size = Align(entry.Size, 4096);
                int firstExtent = extentRows.Count;
                if (size > 0)
                {
                    extentRows.Add(Pack(w =>
                    {
                        w.Write((ulong)(size / sector));
                        w.Write(SuperImage.TargetLinear);
                        w.Write((ulong)(cursor / sector));
                        w.Write(0u);
                    }));
                    placement.Add(new KeyValuePair<long, Entry>(cursor, entry));
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
            if (cursor > totalSize)
                throw new ArgumentException("the partitions do not fit in " + totalSize + " bytes");

            List<byte[]> groupRows = groupNames.Select(g => Pack(w =>
            {
                w.Write(Name(g, 36));
                w.Write(0u);
                w.Write(g == "default" ? 0UL : groups[g]);
            })).ToList();

            byte[] blockDevice = Pack(w =>
            {
                w.Write((ulong)(Align(metadataEnd, Alignment) / sector));
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

            byte[] geometry = new byte[4096];
            using (BinaryWriter w = new BinaryWriter(new MemoryStream(geometry)))
            {
                w.Write(0x616C4467u);
                w.Write(52u);
                w.Write(new byte[32]);
                w.Write(MetadataMaxSize);
                w.Write(SlotCount);
                w.Write(4096u);
            }
            Array.Copy(Sha256(geometry.Take(52).ToArray()), 0, geometry, 8, 32);

            output.SetLength(totalSize);
            WriteAt(output, 4096, geometry);
            WriteAt(output, 8192, geometry);
            for (int copy = 0; copy < 2; copy++)
            {
                for (int slot = 0; slot < SlotCount; slot++)
                {
                    long at = 4096 + 8192 + (long)MetadataMaxSize * (copy * SlotCount + slot);
                    WriteAt(output, at, header);
                    WriteAt(output, at + headerSize, tables);
                }
            }

            byte[] buffer = new byte[1 << 20];
            foreach (KeyValuePair<long, Entry> place in placement)
            {
                output.Position = place.Key;
                place.Value.Data.Position = 0;
                int got;
                while ((got = place.Value.Data.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, got);
            }
        }

        static void Descriptor(BinaryWriter w, int offset, int count, int size)
        {
            w.Write((uint)offset);
            w.Write((uint)count);
            w.Write((uint)size);
        }

        static void WriteAt(Stream output, long offset, byte[] data)
        {
            output.Position = offset;
            output.Write(data, 0, data.Length);
        }

        static byte[] Name(string name, int length)
        {
            byte[] bytes = new byte[length];
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            Array.Copy(ascii, bytes, Math.Min(ascii.Length, length - 1));
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

        static long Align(long value, long alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }
    }
}
