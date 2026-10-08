using System;
using System.IO;

namespace FastbootEnhance.Core.Images
{
    /// <summary>
    /// Writes an Android sparse image in one pass, block by block. A block made of one repeated
    /// 32-bit word becomes a fill chunk (zeros included, so they are written, never left as
    /// "don't care"), other blocks raw data; neighbours of the same kind share a chunk. Skipped
    /// blocks become don't-care chunks. Chunk headers are written first and filled in when the
    /// chunk ends, so the output stream must be seekable.
    /// </summary>
    public sealed class SparseWriter : IDisposable
    {
        const int SparseHeaderSize = 28;
        const int ChunkHeaderSize = 12;

        enum Kind
        {
            None,
            Raw,
            Fill,
            DontCare,
        }

        readonly Stream output;
        readonly BinaryWriter writer;
        readonly int blockSize;
        readonly long totalBlocks;
        readonly long maxRawBlocks;

        Kind kind = Kind.None;
        long chunkHeaderAt;
        long chunkBlocks;
        uint fillWord;

        long blocksWritten;
        int chunkCount;
        bool finished;

        public SparseWriter(Stream output, int blockSize, long totalBlocks)
        {
            if (blockSize < 1024 || blockSize % 4 != 0)
                throw new ArgumentException("block size must be at least 1024 and a multiple of 4", nameof(blockSize));
            if (totalBlocks > uint.MaxValue)
                throw new ArgumentException("too many blocks for the sparse format", nameof(totalBlocks));
            this.output = output;
            this.blockSize = blockSize;
            this.totalBlocks = totalBlocks;
            maxRawBlocks = (uint.MaxValue - ChunkHeaderSize) / blockSize;
            writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
            output.SetLength(0);
            output.Position = SparseHeaderSize;
        }

        public long BlocksWritten => blocksWritten;
        public int Chunks => chunkCount;

        /// <summary>Writes one block from <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
        public void Write(byte[] buffer, int offset)
        {
            if (blocksWritten >= totalBlocks)
                throw new InvalidOperationException("more blocks than the image holds");

            uint word = BitConverter.ToUInt32(buffer, offset);
            bool uniform = true;
            for (int i = 4; i < blockSize; i += 4)
            {
                if (BitConverter.ToUInt32(buffer, offset + i) != word)
                {
                    uniform = false;
                    break;
                }
            }

            if (uniform)
            {
                if (kind != Kind.Fill || fillWord != word)
                {
                    Begin(Kind.Fill);
                    fillWord = word;
                    writer.Write(word);
                }
            }
            else
            {
                if (kind != Kind.Raw || chunkBlocks >= maxRawBlocks)
                    Begin(Kind.Raw);
                writer.Write(buffer, offset, blockSize);
            }
            chunkBlocks++;
            blocksWritten++;
        }

        /// <summary>Leaves <paramref name="blocks"/> blocks out of the image ("don't care").</summary>
        public void Skip(long blocks)
        {
            if (blocks <= 0)
                return;
            if (blocksWritten + blocks > totalBlocks)
                throw new InvalidOperationException("more blocks than the image holds");
            if (kind != Kind.DontCare || chunkBlocks + blocks > uint.MaxValue)
                Begin(Kind.DontCare);
            chunkBlocks += blocks;
            blocksWritten += blocks;
        }

        /// <summary>Skips to the end of the image and writes the file header.</summary>
        public void Finish()
        {
            if (finished)
                return;
            Skip(totalBlocks - blocksWritten);
            End();
            writer.Flush();
            long end = output.Position;
            output.Position = 0;
            writer.Write(SparseImage.Magic);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)SparseHeaderSize);
            writer.Write((ushort)ChunkHeaderSize);
            writer.Write((uint)blockSize);
            writer.Write((uint)totalBlocks);
            writer.Write((uint)chunkCount);
            writer.Write(0u);
            writer.Flush();
            output.Position = end;
            finished = true;
        }

        void Begin(Kind next)
        {
            End();
            kind = next;
            chunkBlocks = 0;
            chunkHeaderAt = output.Position;
            writer.Write(new byte[ChunkHeaderSize]);
        }

        /// <summary>Goes back and fills in the header of the chunk that is open.</summary>
        void End()
        {
            if (kind == Kind.None)
                return;
            writer.Flush();
            long end = output.Position;
            long total = end - chunkHeaderAt;
            output.Position = chunkHeaderAt;
            ushort type = kind == Kind.Raw ? (ushort)SparseChunkType.Raw
                : kind == Kind.Fill ? (ushort)SparseChunkType.Fill
                : (ushort)SparseChunkType.DontCare;
            writer.Write(type);
            writer.Write((ushort)0);
            writer.Write((uint)chunkBlocks);
            writer.Write((uint)total);
            writer.Flush();
            output.Position = end;
            chunkCount++;
            kind = Kind.None;
        }

        public void Dispose()
        {
            writer.Dispose();
        }
    }
}
