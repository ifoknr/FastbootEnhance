using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FastbootEnhance.Core.Images
{
    public sealed class SuperExtent
    {
        internal SuperExtent(long sectors, uint targetType, long targetData, uint targetSource)
        {
            Sectors = sectors;
            TargetType = targetType;
            TargetData = targetData;
            TargetSource = targetSource;
        }

        public long Sectors { get; }

        /// <summary>0: data at <see cref="TargetData"/> on a block device; 1: zeros.</summary>
        public uint TargetType { get; }

        /// <summary>First 512-byte sector of the data on the block device.</summary>
        public long TargetData { get; }

        /// <summary>Index of the block device holding the data (0 is super itself).</summary>
        public uint TargetSource { get; }

        public bool IsZero => TargetType == SuperImage.TargetZero;
    }

    public sealed class SuperPartition
    {
        internal SuperPartition(string name, uint attributes, string group, IReadOnlyList<SuperExtent> extents)
        {
            Name = name;
            Attributes = attributes;
            Group = group;
            Extents = extents;
            Size = extents.Sum(e => e.Sectors) * SuperImage.SectorSize;
        }

        public string Name { get; }
        public uint Attributes { get; }
        public string Group { get; }
        public IReadOnlyList<SuperExtent> Extents { get; }
        public long Size { get; }

        public bool ReadOnly => (Attributes & 1) != 0;

        /// <summary>A slot that holds nothing, such as the inactive "_b" half on many phones.</summary>
        public bool IsEmpty => Size == 0;

        /// <summary>True when every extent lives on super itself, so it can be extracted from this image.</summary>
        public bool OnSuperOnly => Extents.All(e => e.IsZero || e.TargetSource == 0);
    }

    public sealed class SuperGroup
    {
        internal SuperGroup(string name, ulong maximumSize)
        {
            Name = name;
            MaximumSize = maximumSize;
        }

        public string Name { get; }

        /// <summary>The most the group's partitions may hold together; 0 means unlimited.</summary>
        public ulong MaximumSize { get; }
    }

    public sealed class SuperBlockDevice
    {
        internal SuperBlockDevice(string name, ulong size, ulong firstLogicalSector, uint alignment)
        {
            Name = name;
            Size = size;
            FirstLogicalSector = firstLogicalSector;
            Alignment = alignment;
        }

        public string Name { get; }
        public ulong Size { get; }
        public ulong FirstLogicalSector { get; }
        public uint Alignment { get; }
    }

    /// <summary>
    /// The logical partition table of a super image (Android 10+ dynamic partitions), read
    /// the way AOSP's liblp does, with every checksum verified, and lpunpack: extracting the
    /// partitions it holds. Works on any seekable stream, including a <see cref="SparseStream"/>,
    /// so a sparse super.img can be unpacked without expanding it first.
    /// </summary>
    public sealed class SuperImage
    {
        public const int SectorSize = 512;
        public const uint TargetLinear = 0;
        public const uint TargetZero = 1;

        const uint GeometryMagic = 0x616C4467;
        const uint HeaderMagic = 0x414C5030;
        const int ReservedBytes = 4096;
        const int GeometrySize = 4096;
        const int GeometryStructSize = 52;
        const int HeaderV0Size = 128;
        const int HeaderV2Size = 256;

        public uint MetadataMaxSize { get; private set; }
        public uint MetadataSlotCount { get; private set; }
        public uint LogicalBlockSize { get; private set; }
        public ushort MajorVersion { get; private set; }
        public ushort MinorVersion { get; private set; }
        public uint HeaderFlags { get; private set; }
        public int Slot { get; private set; }

        /// <summary>True when the primary copy was damaged and the backup copy was used.</summary>
        public bool UsedBackup { get; private set; }

        public IReadOnlyList<SuperPartition> Partitions { get; private set; }
        public IReadOnlyList<SuperGroup> Groups { get; private set; }
        public IReadOnlyList<SuperBlockDevice> BlockDevices { get; private set; }

        public bool IsVirtualAB => (HeaderFlags & 1) != 0;

        /// <summary>True when the stream has super's geometry block at 4096.</summary>
        public static bool IsSuper(Stream stream)
        {
            if (stream.Length < ReservedBytes + GeometryStructSize)
                return false;
            stream.Position = ReservedBytes;
            byte[] magic = new byte[4];
            return StreamUtil.TryReadExactly(stream, magic) && BitConverter.ToUInt32(magic, 0) == GeometryMagic;
        }

        /// <summary>Reads the metadata of <paramref name="slot"/> (0 for slot "a").</summary>
        public static SuperImage Read(Stream stream, int slot = 0)
        {
            SuperImage image = new SuperImage { Slot = slot };
            byte[] geometry = ReadGeometry(stream, ReservedBytes) ?? ReadGeometry(stream, ReservedBytes + GeometrySize);
            if (geometry == null)
                throw new InvalidDataException("no valid super geometry block (bad magic or checksum in both copies)");

            image.MetadataMaxSize = BitConverter.ToUInt32(geometry, 40);
            image.MetadataSlotCount = BitConverter.ToUInt32(geometry, 44);
            image.LogicalBlockSize = BitConverter.ToUInt32(geometry, 48);
            if (image.MetadataMaxSize == 0 || image.MetadataMaxSize % SectorSize != 0 || image.MetadataMaxSize > (64 << 20))
                throw new InvalidDataException("invalid metadata size in the super geometry");
            if (slot < 0 || slot >= image.MetadataSlotCount)
                throw new ArgumentOutOfRangeException(nameof(slot), "the image has " + image.MetadataSlotCount + " metadata slots");

            long primary = ReservedBytes + GeometrySize * 2L + (long)image.MetadataMaxSize * slot;
            long backup = ReservedBytes + GeometrySize * 2L + (long)image.MetadataMaxSize * image.MetadataSlotCount
                          + (long)image.MetadataMaxSize * slot;

            string problem;
            if (!image.TryReadMetadata(stream, primary, out problem))
            {
                string backupProblem;
                if (!image.TryReadMetadata(stream, backup, out backupProblem))
                    throw new InvalidDataException("super metadata is damaged: " + problem + "; backup copy: " + backupProblem);
                image.UsedBackup = true;
            }
            return image;
        }

        static byte[] ReadGeometry(Stream stream, long offset)
        {
            if (stream.Length < offset + GeometryStructSize)
                return null;
            stream.Position = offset;
            byte[] geometry = new byte[GeometryStructSize];
            if (!StreamUtil.TryReadExactly(stream, geometry))
                return null;
            if (BitConverter.ToUInt32(geometry, 0) != GeometryMagic || BitConverter.ToUInt32(geometry, 4) != GeometryStructSize)
                return null;

            byte[] check = (byte[])geometry.Clone();
            byte[] expected = new byte[32];
            Array.Copy(check, 8, expected, 0, 32);
            Array.Clear(check, 8, 32);
            return Sha256(check, 0, check.Length).SequenceEqual(expected) ? geometry : null;
        }

        bool TryReadMetadata(Stream stream, long offset, out string problem)
        {
            problem = null;
            if (stream.Length < offset + HeaderV0Size)
            {
                problem = "the image ends before the metadata";
                return false;
            }

            stream.Position = offset;
            byte[] fixedPart = new byte[HeaderV0Size];
            if (!StreamUtil.TryReadExactly(stream, fixedPart) || BitConverter.ToUInt32(fixedPart, 0) != HeaderMagic)
            {
                problem = "bad metadata header magic";
                return false;
            }

            ushort major = BitConverter.ToUInt16(fixedPart, 4);
            ushort minor = BitConverter.ToUInt16(fixedPart, 6);
            uint headerSize = BitConverter.ToUInt32(fixedPart, 8);
            if (major != 10 || minor > 2)
            {
                problem = "unsupported metadata version " + major + "." + minor;
                return false;
            }
            if (headerSize != HeaderV0Size && headerSize != HeaderV2Size)
            {
                problem = "unexpected metadata header size " + headerSize;
                return false;
            }

            stream.Position = offset;
            byte[] header = new byte[headerSize];
            if (!StreamUtil.TryReadExactly(stream, header))
            {
                problem = "the image ends inside the metadata header";
                return false;
            }

            byte[] headerCopy = (byte[])header.Clone();
            byte[] headerChecksum = headerCopy.Skip(12).Take(32).ToArray();
            Array.Clear(headerCopy, 12, 32);
            if (!Sha256(headerCopy, 0, headerCopy.Length).SequenceEqual(headerChecksum))
            {
                problem = "metadata header checksum does not match";
                return false;
            }

            uint tablesSize = BitConverter.ToUInt32(header, 44);
            if (tablesSize > MetadataMaxSize)
            {
                problem = "metadata tables are larger than the metadata area";
                return false;
            }
            byte[] tables = new byte[tablesSize];
            stream.Position = offset + headerSize;
            if (!StreamUtil.TryReadExactly(stream, tables))
            {
                problem = "the image ends inside the metadata tables";
                return false;
            }
            if (!Sha256(tables, 0, tables.Length).SequenceEqual(header.Skip(48).Take(32)))
            {
                problem = "metadata tables checksum does not match";
                return false;
            }

            List<byte[]> partitionEntries, extentEntries, groupEntries, deviceEntries;
            if (!Table(header, 80, tables, 52, out partitionEntries, ref problem)
                || !Table(header, 92, tables, 24, out extentEntries, ref problem)
                || !Table(header, 104, tables, 48, out groupEntries, ref problem)
                || !Table(header, 116, tables, 64, out deviceEntries, ref problem))
                return false;

            List<SuperGroup> groups = groupEntries
                .Select(g => new SuperGroup(CString(g, 0, 36), BitConverter.ToUInt64(g, 40)))
                .ToList();

            List<SuperExtent> extents = extentEntries
                .Select(e => new SuperExtent((long)BitConverter.ToUInt64(e, 0), BitConverter.ToUInt32(e, 8),
                    (long)BitConverter.ToUInt64(e, 12), BitConverter.ToUInt32(e, 20)))
                .ToList();

            List<SuperPartition> partitions = new List<SuperPartition>();
            foreach (byte[] p in partitionEntries)
            {
                string name = CString(p, 0, 36);
                uint attributes = BitConverter.ToUInt32(p, 36);
                uint first = BitConverter.ToUInt32(p, 40);
                uint count = BitConverter.ToUInt32(p, 44);
                uint group = BitConverter.ToUInt32(p, 48);
                if ((long)first + count > extents.Count)
                {
                    problem = "partition " + name + " points past the extent table";
                    return false;
                }
                partitions.Add(new SuperPartition(name, attributes,
                    group < groups.Count ? groups[(int)group].Name : "?",
                    extents.Skip((int)first).Take((int)count).ToList()));
            }

            MajorVersion = major;
            MinorVersion = minor;
            HeaderFlags = headerSize >= 132 ? BitConverter.ToUInt32(header, 128) : 0;
            Partitions = partitions;
            Groups = groups;
            BlockDevices = deviceEntries
                // first_logical_sector@0, alignment@8, alignment_offset@12, size@16, partition_name[36]@24, flags@60
                .Select(d => new SuperBlockDevice(CString(d, 24, 36), BitConverter.ToUInt64(d, 16), BitConverter.ToUInt64(d, 0), BitConverter.ToUInt32(d, 8)))
                .ToList();
            return true;
        }

        static bool Table(byte[] header, int at, byte[] tables, int minEntrySize, out List<byte[]> entries, ref string problem)
        {
            entries = new List<byte[]>();
            uint offset = BitConverter.ToUInt32(header, at);
            uint count = BitConverter.ToUInt32(header, at + 4);
            uint entrySize = BitConverter.ToUInt32(header, at + 8);
            if (entrySize < minEntrySize || (ulong)offset + (ulong)count * entrySize > (ulong)tables.Length)
            {
                problem = "a metadata table lies outside the tables area";
                return false;
            }
            for (uint i = 0; i < count; i++)
            {
                byte[] entry = new byte[entrySize];
                Array.Copy(tables, offset + i * entrySize, entry, 0, entrySize);
                entries.Add(entry);
            }
            return true;
        }

        static string CString(byte[] data, int offset, int length)
        {
            int end = Array.IndexOf(data, (byte)0, offset, length);
            if (end < 0)
                end = offset + length;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        static byte[] Sha256(byte[] data, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(data, offset, count);
        }

        /// <summary>
        /// Writes <paramref name="partition"/>'s contents to <paramref name="destination"/>,
        /// following its extents through <paramref name="super"/>. Returns the bytes written.
        /// </summary>
        public static long Extract(Stream super, SuperPartition partition, Stream destination,
            IProgress<long> progress, CancellationToken cancellation)
        {
            if (!partition.OnSuperOnly)
                throw new InvalidDataException(partition.Name + " lives partly on another block device, which this image does not hold");

            byte[] buffer = new byte[1 << 20];
            long done = 0;
            long lastReport = 0;
            foreach (SuperExtent extent in partition.Extents)
            {
                long left = extent.Sectors * SectorSize;
                if (extent.IsZero)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                    while (left > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int take = (int)Math.Min(buffer.Length, left);
                        destination.Write(buffer, 0, take);
                        left -= take;
                        done += take;
                    }
                    continue;
                }

                long source = extent.TargetData * SectorSize;
                if (source + left > super.Length)
                    throw new InvalidDataException(partition.Name + " points past the end of the super image (truncated?)");
                super.Position = source;
                while (left > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, left);
                    int got = super.Read(buffer, 0, want);
                    if (got <= 0)
                        throw new EndOfStreamException("super image ended inside " + partition.Name);
                    destination.Write(buffer, 0, got);
                    left -= got;
                    done += got;
                    if (progress != null && done - lastReport >= (16 << 20))
                    {
                        lastReport = done;
                        progress.Report(done);
                    }
                }
            }
            if (progress != null)
                progress.Report(done);
            return done;
        }
    }
}
