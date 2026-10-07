using System;
using System.Collections.Generic;
using System.IO;
using ChromeosUpdateEngine;
using Google.Protobuf;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>What one partition in the manifest looks like, ready for display.</summary>
    public sealed class PayloadPartitionInfo
    {
        public string Name { get; internal set; }

        /// <summary>Size of the image this partition extracts to, in bytes.</summary>
        public long UnpackedSize { get; internal set; }

        /// <summary>Lower-case hex SHA-256 the finished image must match, or null when absent.</summary>
        public string ExpectedSha256 { get; internal set; }

        public int OperationCount { get; internal set; }

        /// <summary>Distinct codec labels across this partition's operations, e.g. "zstd", "xz".</summary>
        public IReadOnlyList<string> Codecs { get; internal set; }

        /// <summary>The worst support level across this partition's operations.</summary>
        public OperationSupportKind Support { get; internal set; }

        /// <summary>Why this partition cannot be extracted, when it cannot.</summary>
        public string UnsupportedReason { get; internal set; }

        public bool CanExtract => Support == OperationSupportKind.Supported;

        public override string ToString()
        {
            return Name + " (" + UnpackedSize + " bytes, " + OperationCount + " ops)";
        }
    }

    /// <summary>
    /// A parsed Android A/B update payload (the "CrAU" format used by payload.bin).
    /// </summary>
    public sealed class PayloadFile : IDisposable
    {
        const ulong Magic = 0x43724155; // "CrAU"
        const long MaxManifestSize = 256L * 1024 * 1024;

        /// <summary>
        /// Larger than any partition an OTA carries. A manifest claiming more is treated as
        /// damaged rather than allowed to create an enormous output file.
        /// </summary>
        internal const long MaxPartitionBytes = 64L * 1024 * 1024 * 1024;

        readonly bool ownsSource;
        readonly List<PayloadPartitionInfo> partitions = new List<PayloadPartitionInfo>();

        PayloadFile(PayloadSource source, bool ownsSource)
        {
            Source = source;
            this.ownsSource = ownsSource;
        }

        public PayloadSource Source { get; }

        public DeltaArchiveManifest Manifest { get; private set; }

        public ulong FileFormatVersion { get; private set; }

        public uint BlockSize { get; private set; }

        /// <summary>Offset in the payload where operation data blobs start.</summary>
        public long DataStart { get; private set; }

        /// <summary>Length of the data blob region, from the manifest's signature offset.</summary>
        public long DataLength { get; private set; }

        public Signatures MetadataSignature { get; private set; }

        public Signatures PayloadSignature { get; private set; }

        public IReadOnlyList<PayloadPartitionInfo> Partitions => partitions;

        public uint MinorVersion => Manifest != null ? Manifest.MinorVersion : 0;

        /// <summary>
        /// True when any partition needs its previous contents, i.e. this is an incremental
        /// package rather than a full one.
        /// </summary>
        public bool IsIncremental { get; private set; }

        public static PayloadFile Open(string path, string temporaryDirectory = null)
        {
            PayloadSource source = PayloadSource.Open(path, temporaryDirectory);
            try
            {
                return FromSource(source, true);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public static PayloadFile FromSource(PayloadSource source, bool ownsSource)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            PayloadFile payload = new PayloadFile(source, ownsSource);
            using (Stream stream = source.OpenStream())
            {
                payload.Parse(stream);
            }
            payload.BuildPartitionInfo();
            return payload;
        }

        void Parse(Stream stream)
        {
            if (ReadUInt32BigEndian(stream) != Magic)
                throw new PayloadFormatException("not an update payload: magic is not \"CrAU\"");

            FileFormatVersion = ReadUInt64BigEndian(stream);
            if (FileFormatVersion < 2)
            {
                throw new PayloadFormatException(
                    "payload format version " + FileFormatVersion + " is not supported; only version 2 and newer");
            }

            long manifestSize = (long)ReadUInt64BigEndian(stream);
            if (manifestSize <= 0 || manifestSize > MaxManifestSize)
                throw new PayloadFormatException("manifest size " + manifestSize + " is out of range");

            long metadataSignatureSize = ReadUInt32BigEndian(stream);
            if (metadataSignatureSize < 0 || metadataSignatureSize > MaxManifestSize)
                throw new PayloadFormatException("metadata signature size is out of range");

            byte[] manifestBytes = ReadExactly(stream, (int)manifestSize);
            try
            {
                Manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestBytes);
            }
            catch (InvalidProtocolBufferException e)
            {
                throw new PayloadFormatException("the manifest could not be decoded", e);
            }

            if (metadataSignatureSize > 0)
            {
                byte[] signatureBytes = ReadExactly(stream, (int)metadataSignatureSize);
                try
                {
                    MetadataSignature = Signatures.Parser.ParseFrom(signatureBytes);
                }
                catch (InvalidProtocolBufferException)
                {
                    // A signature we cannot decode does not stop us reading the payload.
                    MetadataSignature = null;
                }
            }

            BlockSize = Manifest.HasBlockSize ? Manifest.BlockSize : 4096u;
            if (BlockSize == 0)
                throw new PayloadFormatException("the manifest declares a block size of zero");

            DataStart = stream.Position;
            DataLength = Manifest.HasSignaturesOffset ? (long)Manifest.SignaturesOffset : stream.Length - DataStart;

            if (Manifest.HasSignaturesOffset && Manifest.HasSignaturesSize && Manifest.SignaturesSize > 0)
            {
                long offset = DataStart + (long)Manifest.SignaturesOffset;
                long size = (long)Manifest.SignaturesSize;
                if (size <= MaxManifestSize && offset >= 0 && offset + size <= stream.Length)
                {
                    stream.Position = offset;
                    try
                    {
                        PayloadSignature = Signatures.Parser.ParseFrom(ReadExactly(stream, (int)size));
                    }
                    catch (InvalidProtocolBufferException)
                    {
                        PayloadSignature = null;
                    }
                }
            }
        }

        void BuildPartitionInfo()
        {
            bool incremental = false;

            foreach (PartitionUpdate update in Manifest.Partitions)
            {
                List<string> codecs = new List<string>();
                OperationSupportKind worst = OperationSupportKind.Supported;
                string reason = null;
                long computedEnd = 0;

                foreach (InstallOperation operation in update.Operations)
                {
                    OperationSupportInfo info = OperationSupport.Describe(operation.Type);

                    if (!codecs.Contains(info.Codec))
                        codecs.Add(info.Codec);

                    if (info.Kind > worst)
                    {
                        worst = info.Kind;
                        reason = info.Reason;
                    }

                    try
                    {
                        long end = ExtentWriter.HighestByteOffset(operation.DstExtents, BlockSize);
                        if (end > computedEnd)
                            computedEnd = end;
                    }
                    catch (PayloadFormatException e)
                    {
                        worst = OperationSupportKind.Unsupported;
                        reason = e.Message;
                    }
                }

                if (worst == OperationSupportKind.RequiresSource || worst == OperationSupportKind.Unsupported)
                    incremental = true;

                ulong declared = update.NewPartitionInfo != null && update.NewPartitionInfo.HasSize
                    ? update.NewPartitionInfo.Size
                    : (ulong)computedEnd;

                long size = declared > (ulong)MaxPartitionBytes ? MaxPartitionBytes : (long)declared;
                if (declared > (ulong)MaxPartitionBytes)
                {
                    worst = OperationSupportKind.Unsupported;
                    reason = "the manifest gives this partition " + declared + " bytes, more than any real partition";
                }

                string hash = update.NewPartitionInfo != null && update.NewPartitionInfo.HasHash
                    ? Hex.ToLowerHex(update.NewPartitionInfo.Hash.ToByteArray())
                    : null;

                partitions.Add(new PayloadPartitionInfo
                {
                    Name = update.PartitionName,
                    UnpackedSize = size,
                    ExpectedSha256 = hash,
                    OperationCount = update.Operations.Count,
                    Codecs = codecs,
                    Support = worst,
                    UnsupportedReason = reason
                });
            }

            IsIncremental = incremental;
        }

        /// <summary>How many operations of each type the manifest holds, most common first, by AOSP name.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> OperationCounts()
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PartitionUpdate update in Manifest.Partitions)
            {
                foreach (InstallOperation operation in update.Operations)
                {
                    string label = OperationSupport.Name(operation.Type);
                    int seen;
                    counts.TryGetValue(label, out seen);
                    counts[label] = seen + 1;
                }
            }

            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key));
            return ordered;
        }

        public PayloadPartitionInfo FindPartition(string name)
        {
            for (int i = 0; i < partitions.Count; i++)
            {
                if (string.Equals(partitions[i].Name, name, StringComparison.Ordinal))
                    return partitions[i];
            }
            return null;
        }

        internal PartitionUpdate FindUpdate(string name)
        {
            foreach (PartitionUpdate update in Manifest.Partitions)
            {
                if (string.Equals(update.PartitionName, name, StringComparison.Ordinal))
                    return update;
            }
            return null;
        }

        public void Dispose()
        {
            if (ownsSource)
                Source.Dispose();
        }

        static byte[] ReadExactly(Stream stream, int count)
        {
            return StreamUtil.ReadExactly(stream, count, "the payload ends in the middle of a structure");
        }

        static uint ReadUInt32BigEndian(Stream stream)
        {
            byte[] b = ReadExactly(stream, 4);
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        static ulong ReadUInt64BigEndian(Stream stream)
        {
            byte[] b = ReadExactly(stream, 8);
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value = (value << 8) | b[i];
            return value;
        }
    }

    internal static class Hex
    {
        internal static string ToLowerHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }
}
