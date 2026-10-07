using System;
using System.Collections.Generic;
using System.IO;

namespace FastbootEnhance.Core.Images
{
    public enum SparseChunkType : ushort
    {
        Raw = 0xCAC1,
        Fill = 0xCAC2,
        DontCare = 0xCAC3,
        Crc32 = 0xCAC4,
    }

    /// <summary>One chunk of an Android sparse image, located in its file and in the output.</summary>
    public sealed class SparseChunk
    {
        internal SparseChunk(SparseChunkType type, long outputBlock, long blocks, long dataOffset, long dataLength, uint value)
        {
            Type = type;
            OutputBlock = outputBlock;
            Blocks = blocks;
            DataOffset = dataOffset;
            DataLength = dataLength;
            Value = value;
        }

        public SparseChunkType Type { get; }

        /// <summary>First block of the output image this chunk covers.</summary>
        public long OutputBlock { get; }

        /// <summary>Blocks of output covered; zero for a CRC32 chunk.</summary>
        public long Blocks { get; }

        /// <summary>Where the chunk's data starts in the sparse file.</summary>
        public long DataOffset { get; }

        /// <summary>Bytes of data stored in the sparse file after the chunk header.</summary>
        public long DataLength { get; }

        /// <summary>The fill pattern of a Fill chunk, or the checksum of a CRC32 chunk.</summary>
        public uint Value { get; }
    }

    /// <summary>
    /// An Android sparse image (the format of most factory images and of fastboot transfers),
    /// read without unpacking: the header and an index of its chunks.
    /// </summary>
    public sealed class SparseImage
    {
        public const uint Magic = 0xED26FF3A;
        const int MinFileHeader = 28;
        const int MinChunkHeader = 12;

        SparseImage(string path)
        {
            Path = path;
        }

        public string Path { get; }
        public long FileLength { get; private set; }
        public ushort MajorVersion { get; private set; }
        public ushort MinorVersion { get; private set; }
        public int BlockSize { get; private set; }
        public long TotalBlocks { get; private set; }
        public int TotalChunks { get; private set; }

        /// <summary>CRC-32 of the whole output (don't-care counted as zeros), or 0 when not recorded.</summary>
        public uint ImageChecksum { get; private set; }

        public IReadOnlyList<SparseChunk> Chunks { get; private set; }

        /// <summary>Size of the image once expanded.</summary>
        public long OutputLength => TotalBlocks * BlockSize;

        public long CountBlocks(SparseChunkType type)
        {
            long total = 0;
            foreach (SparseChunk chunk in Chunks)
            {
                if (chunk.Type == type)
                    total += chunk.Blocks;
            }
            return total;
        }

        /// <summary>True when the stream starts with the sparse magic. The position is restored.</summary>
        public static bool IsSparse(Stream stream)
        {
            long position = stream.Position;
            try
            {
                byte[] magic = new byte[4];
                return StreamUtil.TryReadExactly(stream, magic) && BitConverter.ToUInt32(magic, 0) == Magic;
            }
            finally
            {
                stream.Position = position;
            }
        }

        public static bool IsSparse(string path)
        {
            using (FileStream file = File.OpenRead(path))
                return IsSparse(file);
        }

        /// <summary>Reads the header and indexes every chunk, checking that they add up.</summary>
        public static SparseImage Open(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                return Read(file, path);
        }

        static SparseImage Read(Stream stream, string path)
        {
            SparseImage image = new SparseImage(path) { FileLength = stream.Length };
            byte[] header = new byte[MinFileHeader];
            if (!StreamUtil.TryReadExactly(stream, header))
                throw new InvalidDataException("file is too short to be a sparse image");

            uint magic = BitConverter.ToUInt32(header, 0);
            if (magic != Magic)
                throw new InvalidDataException("not an Android sparse image (bad magic)");

            image.MajorVersion = BitConverter.ToUInt16(header, 4);
            image.MinorVersion = BitConverter.ToUInt16(header, 6);
            int fileHeaderSize = BitConverter.ToUInt16(header, 8);
            int chunkHeaderSize = BitConverter.ToUInt16(header, 10);
            uint blockSize = BitConverter.ToUInt32(header, 12);
            uint totalBlocks = BitConverter.ToUInt32(header, 16);
            uint totalChunks = BitConverter.ToUInt32(header, 20);
            image.ImageChecksum = BitConverter.ToUInt32(header, 24);

            if (image.MajorVersion != 1)
                throw new InvalidDataException("unsupported sparse format version " + image.MajorVersion + "." + image.MinorVersion);
            if (fileHeaderSize < MinFileHeader || chunkHeaderSize < MinChunkHeader)
                throw new InvalidDataException("sparse headers are smaller than the format allows");
            if (blockSize == 0 || blockSize % 4 != 0 || blockSize > (1 << 24))
                throw new InvalidDataException("invalid sparse block size " + blockSize);

            image.BlockSize = (int)blockSize;
            image.TotalBlocks = totalBlocks;
            image.TotalChunks = (int)Math.Min(totalChunks, int.MaxValue);

            List<SparseChunk> chunks = new List<SparseChunk>((int)Math.Min(totalChunks, 1 << 20));
            long position = fileHeaderSize;
            long outputBlock = 0;
            byte[] chunkHeader = new byte[MinChunkHeader];
            byte[] word = new byte[4];

            for (uint i = 0; i < totalChunks; i++)
            {
                stream.Position = position;
                if (!StreamUtil.TryReadExactly(stream, chunkHeader))
                    throw new InvalidDataException("sparse image ends inside chunk " + i + " of " + totalChunks);

                ushort type = BitConverter.ToUInt16(chunkHeader, 0);
                uint blocks = BitConverter.ToUInt32(chunkHeader, 4);
                uint totalSize = BitConverter.ToUInt32(chunkHeader, 8);
                if (totalSize < chunkHeaderSize)
                    throw new InvalidDataException("chunk " + i + " is smaller than its own header");

                long dataOffset = position + chunkHeaderSize;
                long dataLength = totalSize - (long)chunkHeaderSize;
                if (dataOffset + dataLength > stream.Length)
                    throw new InvalidDataException("chunk " + i + " runs past the end of the file (truncated image?)");

                uint value = 0;
                switch ((SparseChunkType)type)
                {
                    case SparseChunkType.Raw:
                        if (dataLength != (long)blocks * blockSize)
                            throw new InvalidDataException("raw chunk " + i + " holds " + dataLength + " bytes for " + blocks + " blocks");
                        break;
                    case SparseChunkType.Fill:
                    case SparseChunkType.Crc32:
                        if (dataLength < 4)
                            throw new InvalidDataException("chunk " + i + " is missing its 4-byte value");
                        stream.Position = dataOffset;
                        if (!StreamUtil.TryReadExactly(stream, word))
                            throw new InvalidDataException("chunk " + i + " is missing its 4-byte value");
                        value = BitConverter.ToUInt32(word, 0);
                        break;
                    case SparseChunkType.DontCare:
                        break;
                    default:
                        throw new InvalidDataException("unknown chunk type 0x" + type.ToString("X4") + " in chunk " + i);
                }

                long covered = (SparseChunkType)type == SparseChunkType.Crc32 ? 0 : blocks;
                chunks.Add(new SparseChunk((SparseChunkType)type, outputBlock, covered, dataOffset, dataLength, value));
                outputBlock += covered;
                if (outputBlock > totalBlocks)
                    throw new InvalidDataException("chunks cover more than the " + totalBlocks + " blocks the header declares");
                position = dataOffset + dataLength;
            }

            if (outputBlock != totalBlocks)
                throw new InvalidDataException("chunks cover " + outputBlock + " blocks but the header declares " + totalBlocks);

            image.Chunks = chunks;
            return image;
        }
    }
}
