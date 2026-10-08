using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace FastbootEnhance.Core.Images
{
    /// <summary>How the partitions of a super image are arranged.</summary>
    public enum SuperSlotMode
    {
        /// <summary>A/B with Virtual A/B (Android 11 and later phones): two groups, header flag set.</summary>
        VirtualAB,

        /// <summary>A/B without Virtual A/B: two groups that each get half of super.</summary>
        AB,

        /// <summary>One slot (non-A/B phones): one group, no suffixes.</summary>
        Single,

        /// <summary>Copied from an existing super image: its groups, partitions and settings.</summary>
        Imported,
    }

    public sealed class SuperPlanGroup
    {
        public SuperPlanGroup(string name, ulong maximumSize)
        {
            Name = name;
            MaximumSize = maximumSize;
        }

        public string Name { get; }

        /// <summary>The most its partitions may take together; 0 means no limit.</summary>
        public ulong MaximumSize { get; set; }
    }

    public sealed class SuperPlanPartition
    {
        internal SuperPlanPartition(string name, string group)
        {
            Name = name;
            Group = group;
        }

        public string Name { get; }
        public string Group { get; internal set; }
        public bool ReadOnly { get; set; } = true;

        /// <summary>The image file (the first part, for a split sparse image); null when empty.</summary>
        public string ImagePath { get; private set; }

        /// <summary>Size of the image once expanded.</summary>
        public long ImageLength { get; private set; }

        public bool ImageIsSparse { get; private set; }
        public ImageKind ImageKind { get; private set; }

        public bool HasImage => ImagePath != null;

        /// <summary>What the partition takes in super: the image rounded up to whole blocks.</summary>
        public long Allocated => SuperBuilder.Align(ImageLength, SuperBuilder.BlockSize);

        public void SetImage(string path)
        {
            ImageInfo info = ImageProbe.Identify(path);
            ImagePath = path;
            ImageLength = info.ImageLength;
            ImageIsSparse = info.IsSparse;
            ImageKind = info.Kind;
        }

        public void ClearImage()
        {
            ImagePath = null;
            ImageLength = 0;
            ImageIsSparse = false;
            ImageKind = ImageKind.Unknown;
        }

        /// <summary>The expanded image: sparse images (split ones too) are read through.</summary>
        internal Stream OpenImage()
        {
            if (ImageIsSparse)
                return SparseStream.Open(SparseConverter.FindParts(ImagePath));
            return new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        }
    }

    public enum SuperProblemKind
    {
        NoSize,
        SizeNotAligned,
        MetadataNotAligned,
        MetadataTooLarge,
        NoImages,
        BadName,
        DuplicateName,
        UnknownGroup,
        GroupFull,
        DoesNotFit,
    }

    /// <summary>Something that stops the image from being built; the app words it.</summary>
    public sealed class SuperProblem
    {
        internal SuperProblem(SuperProblemKind kind, string subject = null, long need = 0, long have = 0)
        {
            Kind = kind;
            Subject = subject;
            Need = need;
            Have = have;
        }

        public SuperProblemKind Kind { get; }

        /// <summary>The partition or group concerned, when there is one.</summary>
        public string Subject { get; }

        public long Need { get; }
        public long Have { get; }

        public override string ToString()
        {
            return Kind + (Subject != null ? " " + Subject : "") + (Need > 0 ? " need " + Need + " have " + Have : "");
        }
    }

    public sealed class SuperPlanCheck
    {
        internal SuperPlanCheck()
        {
        }

        public long DeviceSize { get; internal set; }

        /// <summary>Where partitions can start (after the metadata, aligned).</summary>
        public long FirstUsable { get; internal set; }

        /// <summary>Where the last partition ends, aligned: what the layout needs.</summary>
        public long UsedEnd { get; internal set; }

        public long DataBytes { get; internal set; }
        public IReadOnlyDictionary<string, long> GroupUse { get; internal set; }
        public IReadOnlyList<SuperProblem> Problems { get; internal set; }
        public bool CanBuild => Problems.Count == 0;
    }

    public sealed class SuperVerifyResult
    {
        internal SuperVerifyResult()
        {
        }

        public IReadOnlyList<string> Problems { get; internal set; }
        public bool Ok => Problems.Count == 0;

        /// <summary>SHA-256 of each partition's image as read back from the built super.</summary>
        public IReadOnlyDictionary<string, string> Sha256 { get; internal set; }

        public long BytesChecked { get; internal set; }
    }

    /// <summary>
    /// What to put in a super image and how: the settings lpmake takes (size, metadata, slots,
    /// groups) and one entry per partition, with or without an image. Builds the image and
    /// reads it back to check every partition.
    /// </summary>
    public sealed class SuperPlan
    {
        public const uint DefaultMetadataSize = 65536;
        public const uint DefaultAlignment = 1 << 20;

        static readonly Regex ValidName = new Regex("^[A-Za-z0-9_.-]{1,36}$");
        static readonly Regex SplitPart = new Regex(@"^(?<stem>.+?)(\.img)?([._]sparsechunk)?\.\d+$", RegexOptions.IgnoreCase);
        static readonly string[] Extensions = { ".img", ".simg", ".raw", ".bin", ".ext4", ".erofs" };

        public long DeviceSize { get; set; }
        public uint MetadataMaxSize { get; set; } = DefaultMetadataSize;
        public uint MetadataSlots { get; set; } = 3;
        public uint Alignment { get; set; } = DefaultAlignment;
        public uint HeaderFlags { get; set; }
        public bool ExpandedHeader { get; set; }
        public SuperSlotMode Mode { get; private set; }

        /// <summary>The group name without its slot suffix ("main" gives main_a and main_b).</summary>
        public string GroupBase { get; private set; } = "main";

        /// <summary>When true, group sizes follow the super size (see <see cref="AutoGroupSize"/>).</summary>
        public bool GroupSizeAuto { get; set; } = true;

        public List<SuperPlanGroup> Groups { get; } = new List<SuperPlanGroup>();
        public List<SuperPlanPartition> Partitions { get; } = new List<SuperPlanPartition>();

        public bool VirtualAB => (HeaderFlags & 1) != 0;

        /// <summary>True when partitions carry _a/_b suffixes.</summary>
        public bool Slotted => Mode == SuperSlotMode.VirtualAB || Mode == SuperSlotMode.AB
            || (Mode == SuperSlotMode.Imported && Partitions.Any(p => SlotSuffix(p.Name) != null));

        /// <summary>A new plan laid out the way the AOSP build does for this kind of phone.</summary>
        public static SuperPlan Create(SuperSlotMode mode, long deviceSize, string groupBase = "main")
        {
            if (mode == SuperSlotMode.Imported)
                throw new ArgumentException("use FromImage for an imported layout", nameof(mode));
            SuperPlan plan = new SuperPlan { DeviceSize = deviceSize };
            plan.Arrange(mode, groupBase);
            return plan;
        }

        /// <summary>
        /// The layout of an existing super image: its size, metadata settings, groups and
        /// partitions (empty, ready for images), so a modified copy can be built.
        /// </summary>
        public static SuperPlan FromImage(SuperImage image)
        {
            if (image.BlockDevices.Count != 1)
                throw new NotSupportedException("super spread over several block devices (retrofit) cannot be rebuilt");
            SuperBlockDevice device = image.BlockDevices[0];

            SuperPlan plan = new SuperPlan
            {
                Mode = SuperSlotMode.Imported,
                DeviceSize = (long)device.Size,
                MetadataMaxSize = image.MetadataMaxSize,
                MetadataSlots = image.MetadataSlotCount,
                Alignment = device.Alignment != 0 && device.Alignment % SuperBuilder.BlockSize == 0 ? device.Alignment : DefaultAlignment,
                HeaderFlags = image.HeaderFlags,
                ExpandedHeader = image.MinorVersion >= 2,
                GroupSizeAuto = false,
            };
            foreach (SuperGroup group in image.Groups.Where(g => g.Name != "default"))
                plan.Groups.Add(new SuperPlanGroup(group.Name, group.MaximumSize));
            foreach (SuperPartition partition in image.Partitions)
                plan.Partitions.Add(new SuperPlanPartition(partition.Name, partition.Group) { ReadOnly = partition.ReadOnly });

            string first = plan.Groups.Select(g => g.Name).FirstOrDefault();
            if (first != null)
                plan.GroupBase = SlotSuffix(first) != null ? first.Substring(0, first.Length - 2) : first;
            return plan;
        }

        /// <summary>
        /// Switches to a standard layout, keeping the images: each is added again under its
        /// name without a slot suffix.
        /// </summary>
        public void Arrange(SuperSlotMode mode, string groupBase)
        {
            if (mode == SuperSlotMode.Imported)
                throw new ArgumentException("an imported layout comes from FromImage", nameof(mode));

            List<SuperPlanPartition> withImages = Partitions.Where(p => p.HasImage).ToList();
            Mode = mode;
            GroupBase = string.IsNullOrWhiteSpace(groupBase) ? "main" : groupBase.Trim();
            MetadataSlots = mode == SuperSlotMode.Single ? 2u : 3u;
            HeaderFlags = mode == SuperSlotMode.VirtualAB ? 1u : 0u;
            ExpandedHeader = mode == SuperSlotMode.VirtualAB;
            MetadataMaxSize = DefaultMetadataSize;
            Alignment = DefaultAlignment;
            GroupSizeAuto = true;

            Groups.Clear();
            Partitions.Clear();
            if (mode == SuperSlotMode.Single)
            {
                Groups.Add(new SuperPlanGroup(GroupBase, 0));
            }
            else
            {
                Groups.Add(new SuperPlanGroup(GroupBase + "_a", 0));
                Groups.Add(new SuperPlanGroup(GroupBase + "_b", 0));
            }
            UpdateAutoGroupSize();

            foreach (SuperPlanPartition old in withImages)
            {
                string name = old.Name;
                if (SlotSuffix(name) == "_a")
                    name = name.Substring(0, name.Length - 2);
                else if (SlotSuffix(name) == "_b")
                    continue;   // the other slot's image; a new layout fills slot a
                SuperPlanPartition added = Find(name, create: true);
                added.SetImage(old.ImagePath);
            }
        }

        /// <summary>
        /// The group size the build would use for this super: everything after the metadata,
        /// or half of it for each slot on A/B without Virtual A/B.
        /// </summary>
        public static ulong AutoGroupSize(SuperSlotMode mode, long deviceSize, uint metadataMaxSize, uint slots, uint alignment)
        {
            long first = SuperBuilder.Align(SuperBuilder.MetadataAreaSize(metadataMaxSize, slots), alignment);
            long room = deviceSize - first;
            if (room <= 0)
                return 0;
            if (mode == SuperSlotMode.AB)
                room /= 2;
            return (ulong)(room / SuperBuilder.BlockSize * SuperBuilder.BlockSize);
        }

        public void UpdateAutoGroupSize()
        {
            if (!GroupSizeAuto || Mode == SuperSlotMode.Imported)
                return;
            ulong size = AutoGroupSize(Mode, DeviceSize, MetadataMaxSize, MetadataSlots, Alignment);
            foreach (SuperPlanGroup group in Groups)
                group.MaximumSize = size;
        }

        /// <summary>The partition name a file stands for: "system.img" and "system.img_sparsechunk.0" give "system".</summary>
        public static string NameFromFile(string path)
        {
            string name = Path.GetFileName(path);
            Match split = SplitPart.Match(name);
            if (split.Success)
                name = split.Groups["stem"].Value;
            foreach (string extension in Extensions)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length)
                {
                    name = name.Substring(0, name.Length - extension.Length);
                    break;
                }
            }
            return name;
        }

        static string SlotSuffix(string name)
        {
            if (name.Length > 2 && (name.EndsWith("_a", StringComparison.Ordinal) || name.EndsWith("_b", StringComparison.Ordinal)))
                return name.Substring(name.Length - 2);
            return null;
        }

        /// <summary>
        /// Adds an image file: it fills the partition of the same name (slot a when the file
        /// has no suffix and the layout has slots), which is created when missing.
        /// </summary>
        public SuperPlanPartition AddImage(string path)
        {
            return AddImage(path, NameFromFile(path));
        }

        public SuperPlanPartition AddImage(string path, string name)
        {
            SuperPlanPartition target = Find(name, create: true);
            target.SetImage(path);
            return target;
        }

        /// <summary>
        /// Gives every partition still without an image the file named after it in
        /// <paramref name="folder"/> ("system_a.img", or "system.img" for system_a), as the
        /// Image Tools extractor writes them. Returns how many were found.
        /// </summary>
        public int AttachFolder(string folder)
        {
            int found = 0;
            foreach (SuperPlanPartition partition in Partitions.Where(p => !p.HasImage).ToList())
            {
                List<string> candidates = new List<string> { partition.Name };
                if (SlotSuffix(partition.Name) == "_a")
                    candidates.Add(partition.Name.Substring(0, partition.Name.Length - 2));
                foreach (string candidate in candidates)
                {
                    string file = Path.Combine(folder, candidate + ".img");
                    if (File.Exists(file))
                    {
                        partition.SetImage(file);
                        found++;
                        break;
                    }
                }
            }
            return found;
        }

        public void Remove(SuperPlanPartition partition)
        {
            Partitions.Remove(partition);
            if (Mode == SuperSlotMode.Imported)
                return;
            // Its partner in the other slot goes too when it holds nothing.
            string suffix = SlotSuffix(partition.Name);
            if (suffix == null)
                return;
            string other = partition.Name.Substring(0, partition.Name.Length - 2) + (suffix == "_a" ? "_b" : "_a");
            SuperPlanPartition partner = Partitions.FirstOrDefault(p => p.Name == other);
            if (partner != null && !partner.HasImage)
                Partitions.Remove(partner);
        }

        /// <summary>Finds the partition an image name refers to, creating it (with its other slot) if asked.</summary>
        SuperPlanPartition Find(string name, bool create)
        {
            SuperPlanPartition exact = Partitions.FirstOrDefault(p => p.Name == name);
            if (exact != null)
                return exact;

            bool slotted = Slotted;
            if (slotted && SlotSuffix(name) == null)
            {
                SuperPlanPartition slotA = Partitions.FirstOrDefault(p => p.Name == name + "_a");
                if (slotA != null)
                    return slotA;
            }
            if (!create)
                return null;

            if (!slotted)
            {
                SuperPlanPartition single = new SuperPlanPartition(name, GroupFor(null));
                Partitions.Add(single);
                return single;
            }

            string suffix = SlotSuffix(name);
            string stem = suffix == null ? name : name.Substring(0, name.Length - 2);
            SuperPlanPartition a = Partitions.FirstOrDefault(p => p.Name == stem + "_a");
            if (a == null)
            {
                a = new SuperPlanPartition(stem + "_a", GroupFor("_a"));
                Partitions.Add(a);
            }
            SuperPlanPartition b = Partitions.FirstOrDefault(p => p.Name == stem + "_b");
            if (b == null)
            {
                b = new SuperPlanPartition(stem + "_b", GroupFor("_b"));
                Partitions.Add(b);
            }
            return suffix == "_b" ? b : a;
        }

        string GroupFor(string suffix)
        {
            SuperPlanGroup match = suffix == null ? null : Groups.FirstOrDefault(g => g.Name.EndsWith(suffix, StringComparison.Ordinal));
            if (match == null)
                match = Groups.FirstOrDefault(g => SlotSuffix(g.Name) == null) ?? Groups.FirstOrDefault();
            return match == null ? "default" : match.Name;
        }

        /// <summary>Checks everything lpmake would, and works out how much of super the layout uses.</summary>
        public SuperPlanCheck Check()
        {
            List<SuperProblem> problems = new List<SuperProblem>();
            if (DeviceSize <= 0)
                problems.Add(new SuperProblem(SuperProblemKind.NoSize));
            else if (DeviceSize % SuperBuilder.BlockSize != 0)
                problems.Add(new SuperProblem(SuperProblemKind.SizeNotAligned, null, SuperBuilder.BlockSize, DeviceSize));
            if (MetadataMaxSize == 0 || MetadataMaxSize % SuperBuilder.BlockSize != 0)
                problems.Add(new SuperProblem(SuperProblemKind.MetadataNotAligned, null, SuperBuilder.BlockSize, MetadataMaxSize));

            int extents = Partitions.Count(p => p.HasImage && p.ImageLength > 0);
            long metadataBytes = (ExpandedHeader ? 256 : 128) + 52L * Partitions.Count + 24L * extents
                                 + 48L * (Groups.Count(g => g.Name != "default") + 1) + 64;
            if (metadataBytes > MetadataMaxSize)
                problems.Add(new SuperProblem(SuperProblemKind.MetadataTooLarge, null, metadataBytes, MetadataMaxSize));

            if (!Partitions.Any(p => p.HasImage && p.ImageLength > 0))
                problems.Add(new SuperProblem(SuperProblemKind.NoImages));

            HashSet<string> seen = new HashSet<string>();
            foreach (SuperPlanPartition partition in Partitions)
            {
                if (!ValidName.IsMatch(partition.Name))
                    problems.Add(new SuperProblem(SuperProblemKind.BadName, partition.Name));
                if (!seen.Add(partition.Name))
                    problems.Add(new SuperProblem(SuperProblemKind.DuplicateName, partition.Name));
                if (partition.Group != "default" && !Groups.Any(g => g.Name == partition.Group))
                    problems.Add(new SuperProblem(SuperProblemKind.UnknownGroup, partition.Group));
            }
            foreach (SuperPlanGroup group in Groups)
            {
                if (!ValidName.IsMatch(group.Name))
                    problems.Add(new SuperProblem(SuperProblemKind.BadName, group.Name));
            }

            Dictionary<string, long> use = new Dictionary<string, long>();
            foreach (SuperPlanGroup group in Groups)
                use[group.Name] = 0;
            foreach (SuperPlanPartition partition in Partitions.Where(p => p.HasImage))
            {
                long had;
                use.TryGetValue(partition.Group, out had);
                use[partition.Group] = had + partition.Allocated;
            }
            foreach (SuperPlanGroup group in Groups)
            {
                if (group.MaximumSize > 0 && use[group.Name] > (long)group.MaximumSize)
                    problems.Add(new SuperProblem(SuperProblemKind.GroupFull, group.Name, use[group.Name], (long)group.MaximumSize));
            }

            long alignment = Alignment == 0 ? DefaultAlignment : Alignment;
            long first = SuperBuilder.Align(SuperBuilder.MetadataAreaSize(MetadataMaxSize, MetadataSlots), alignment);
            long cursor = first;
            long lastEnd = first;
            foreach (SuperPlanPartition partition in Partitions.Where(p => p.HasImage && p.ImageLength > 0))
            {
                lastEnd = cursor + partition.Allocated;
                cursor = SuperBuilder.Align(lastEnd, alignment);
            }
            if (DeviceSize > 0 && lastEnd > DeviceSize)
                problems.Add(new SuperProblem(SuperProblemKind.DoesNotFit, null, lastEnd, DeviceSize));

            return new SuperPlanCheck
            {
                DeviceSize = DeviceSize,
                FirstUsable = first,
                UsedEnd = Partitions.Any(p => p.HasImage && p.ImageLength > 0) ? cursor : first,
                DataBytes = Partitions.Where(p => p.HasImage).Sum(p => p.ImageLength),
                GroupUse = use,
                Problems = problems,
            };
        }

        SuperBuilder ToBuilder(List<Stream> opened)
        {
            SuperBuilder builder = new SuperBuilder
            {
                MetadataMaxSize = MetadataMaxSize,
                SlotCount = MetadataSlots,
                Alignment = Alignment == 0 ? DefaultAlignment : Alignment,
                ExpandedHeader = ExpandedHeader || HeaderFlags != 0,
                HeaderFlags = HeaderFlags,
            };
            foreach (SuperPlanGroup group in Groups.Where(g => g.Name != "default"))
                builder.AddGroup(group.Name, group.MaximumSize);
            foreach (SuperPlanPartition partition in Partitions)
            {
                Stream data = null;
                if (partition.HasImage && partition.ImageLength > 0)
                {
                    data = partition.OpenImage();
                    opened.Add(data);
                }
                builder.AddPartition(partition.Name, partition.Group, data, partition.ReadOnly);
            }
            return builder;
        }

        /// <summary>Writes the image. <paramref name="progress"/> counts bytes of partition data.</summary>
        public SuperLayout Build(string output, bool sparse, IProgress<long> progress, CancellationToken cancellation)
        {
            SuperPlanCheck check = Check();
            if (!check.CanBuild)
                throw new InvalidOperationException("the layout has problems: " + string.Join(", ", check.Problems));

            List<Stream> opened = new List<Stream>();
            try
            {
                SuperBuilder builder = ToBuilder(opened);
                return builder.WriteFile(output, DeviceSize, sparse, progress, cancellation);
            }
            finally
            {
                foreach (Stream stream in opened)
                    stream.Dispose();
            }
        }

        /// <summary>
        /// Reads a built image back: every metadata slot must parse with the same settings,
        /// groups and partitions as this plan, and every partition must hold exactly its image
        /// (zero padding included). <paramref name="progress"/> counts bytes compared.
        /// </summary>
        public SuperVerifyResult Verify(string output, IProgress<long> progress, CancellationToken cancellation)
        {
            List<string> problems = new List<string>();
            Dictionary<string, string> hashes = new Dictionary<string, string>();
            long checkedBytes = 0;

            using (Stream image = SparseImage.IsSparse(output) ? (Stream)SparseStream.Open(output)
                       : new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                if (image.Length != DeviceSize)
                    problems.Add("image is " + image.Length + " bytes, expected " + DeviceSize);

                SuperImage read = SuperImage.Read(image);
                if (read.UsedBackup)
                    problems.Add("the primary metadata did not read back");
                for (int slot = 1; slot < read.MetadataSlotCount; slot++)
                {
                    SuperImage other = SuperImage.Read(image, slot);
                    if (other.UsedBackup || other.Partitions.Count != read.Partitions.Count)
                        problems.Add("metadata slot " + slot + " does not match slot 0");
                }

                if (read.MetadataSlotCount != MetadataSlots)
                    problems.Add("metadata slots " + read.MetadataSlotCount + ", expected " + MetadataSlots);
                if (read.MetadataMaxSize != MetadataMaxSize)
                    problems.Add("metadata size " + read.MetadataMaxSize + ", expected " + MetadataMaxSize);
                if (read.HeaderFlags != HeaderFlags)
                    problems.Add("header flags " + read.HeaderFlags + ", expected " + HeaderFlags);
                if (read.BlockDevices.Count != 1 || (long)read.BlockDevices[0].Size != DeviceSize)
                    problems.Add("the block device size does not match");

                List<string> wantedGroups = new[] { "default" }.Concat(Groups.Select(g => g.Name).Where(g => g != "default")).ToList();
                if (!read.Groups.Select(g => g.Name).SequenceEqual(wantedGroups))
                    problems.Add("groups are " + string.Join(", ", read.Groups.Select(g => g.Name)));
                foreach (SuperPlanGroup group in Groups)
                {
                    SuperGroup got = read.Groups.FirstOrDefault(g => g.Name == group.Name);
                    if (got != null && got.MaximumSize != group.MaximumSize)
                        problems.Add("group " + group.Name + " has maximum " + got.MaximumSize + ", expected " + group.MaximumSize);
                }

                if (!read.Partitions.Select(p => p.Name).SequenceEqual(Partitions.Select(p => p.Name)))
                    problems.Add("partitions are " + string.Join(", ", read.Partitions.Select(p => p.Name)));

                foreach (SuperPlanPartition wanted in Partitions)
                {
                    SuperPartition got = read.Partitions.FirstOrDefault(p => p.Name == wanted.Name);
                    if (got == null)
                        continue;
                    if (got.Group != wanted.Group)
                        problems.Add(wanted.Name + " is in group " + got.Group + ", expected " + wanted.Group);
                    long expectedSize = wanted.HasImage ? wanted.Allocated : 0;
                    if (got.Size != expectedSize)
                    {
                        problems.Add(wanted.Name + " is " + got.Size + " bytes, expected " + expectedSize);
                        continue;
                    }
                    if (expectedSize == 0)
                        continue;

                    using (Stream source = wanted.OpenImage())
                    using (ComparingStream compare = new ComparingStream(source, wanted.ImageLength))
                    {
                        long before = checkedBytes;
                        SuperImage.Extract(image, got, compare, progress == null ? null : new SyncProgress(done => progress.Report(before + done)), cancellation);
                        checkedBytes += got.Size;
                        if (compare.MismatchAt >= 0)
                            problems.Add(wanted.Name + " differs from its image at byte " + compare.MismatchAt);
                        hashes[wanted.Name] = compare.Sha256();
                    }
                }
            }

            return new SuperVerifyResult { Problems = problems, Sha256 = hashes, BytesChecked = checkedBytes };
        }

        /// <summary>Reports on the calling thread, unlike Progress&lt;T&gt;.</summary>
        sealed class SyncProgress : IProgress<long>
        {
            readonly Action<long> report;

            public SyncProgress(Action<long> report)
            {
                this.report = report;
            }

            public void Report(long value)
            {
                report(value);
            }
        }

        /// <summary>
        /// A write-only stream that checks what is written against an expected stream (and
        /// zeros after its end), hashing the expected part as it goes.
        /// </summary>
        sealed class ComparingStream : Stream
        {
            readonly Stream expected;
            readonly long expectedLength;
            readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 20];
            long position;

            public ComparingStream(Stream expected, long expectedLength)
            {
                this.expected = expected;
                this.expectedLength = expectedLength;
                expected.Position = 0;
            }

            /// <summary>Offset of the first difference, or -1.</summary>
            public long MismatchAt { get; private set; } = -1;

            public string Sha256()
            {
                return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "").ToLowerInvariant();
            }

            public override void Write(byte[] data, int offset, int count)
            {
                while (count > 0)
                {
                    int take = Math.Min(count, buffer.Length);
                    long inExpected = Math.Max(0, Math.Min(take, expectedLength - position));
                    int got = 0;
                    while (got < inExpected)
                    {
                        int n = expected.Read(buffer, got, (int)inExpected - got);
                        if (n <= 0)
                            break;
                        got += n;
                    }
                    if (got > 0)
                        hash.AppendData(data, offset, got);

                    if (MismatchAt < 0)
                    {
                        for (int i = 0; i < take; i++)
                        {
                            byte want = i < got ? buffer[i] : (byte)0;
                            if (data[offset + i] != want)
                            {
                                MismatchAt = position + i;
                                break;
                            }
                        }
                    }
                    position += take;
                    offset += take;
                    count -= take;
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => position;

            public override long Position
            {
                get => position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    hash.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
