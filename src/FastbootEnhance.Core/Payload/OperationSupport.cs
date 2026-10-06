using ChromeosUpdateEngine;

namespace FastbootEnhance.Core.Payload
{
    public enum OperationSupportKind
    {
        /// <summary>Can be produced from the payload alone.</summary>
        Supported,

        /// <summary>Needs the partition's previous contents (incremental OTA).</summary>
        RequiresSource,

        /// <summary>A binary-diff format this library does not implement.</summary>
        Unsupported
    }

    public readonly struct OperationSupportInfo
    {
        public OperationSupportKind Kind { get; }

        /// <summary>Short codec label for the UI, e.g. "zstd", "xz", "raw".</summary>
        public string Codec { get; }

        public string Reason { get; }

        public OperationSupportInfo(OperationSupportKind kind, string codec, string reason)
        {
            Kind = kind;
            Codec = codec;
            Reason = reason;
        }

        public bool IsSupported => Kind == OperationSupportKind.Supported;
    }

    public static class OperationSupport
    {
        public static OperationSupportInfo Describe(InstallOperation.Types.Type type)
        {
            switch (type)
            {
                case InstallOperation.Types.Type.Replace:
                    return Ok("raw");
                case InstallOperation.Types.Type.ReplaceBz:
                    return Ok("bzip2");
                case InstallOperation.Types.Type.ReplaceXz:
                    return Ok("xz");
                case InstallOperation.Types.Type.Zstd:
                    return Ok("zstd");
                case InstallOperation.Types.Type.Zero:
                    return Ok("zero");
                case InstallOperation.Types.Type.Discard:
                    return Ok("discard");

                case InstallOperation.Types.Type.SourceCopy:
                    return Source("copy", "copies blocks from the partition's previous contents");
                case InstallOperation.Types.Type.Move:
                    return Source("move", "moves blocks within the partition's previous contents");

                case InstallOperation.Types.Type.Bsdiff:
                    return No("bsdiff");
                case InstallOperation.Types.Type.SourceBsdiff:
                    return No("bsdiff");
                case InstallOperation.Types.Type.BrotliBsdiff:
                    return No("brotli-bsdiff");
                case InstallOperation.Types.Type.Puffdiff:
                    return No("puffdiff");
                case InstallOperation.Types.Type.Zucchini:
                    return No("zucchini");
                case InstallOperation.Types.Type.Lz4DiffBsdiff:
                    return No("lz4diff-bsdiff");
                case InstallOperation.Types.Type.Lz4DiffPuffdiff:
                    return No("lz4diff-puffdiff");

                default:
                    return new OperationSupportInfo(
                        OperationSupportKind.Unsupported,
                        "unknown",
                        "operation type " + (int)type + " is not known to this build");
            }
        }

        static OperationSupportInfo Ok(string codec)
        {
            return new OperationSupportInfo(OperationSupportKind.Supported, codec, null);
        }

        static OperationSupportInfo Source(string codec, string why)
        {
            return new OperationSupportInfo(OperationSupportKind.RequiresSource, codec, why);
        }

        static OperationSupportInfo No(string codec)
        {
            return new OperationSupportInfo(
                OperationSupportKind.Unsupported,
                codec,
                codec + " is a binary-diff format; only full OTA packages can be extracted");
        }
    }
}
