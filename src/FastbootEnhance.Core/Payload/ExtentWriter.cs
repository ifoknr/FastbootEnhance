using System;
using System.Collections.Generic;
using System.IO;
using ChromeosUpdateEngine;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// Places an operation's decoded bytes into its destination extents.
    /// An operation may name several extents; its data fills them in order, which is
    /// why a single-extent-only reader rejects modern payloads.
    /// </summary>
    internal static class ExtentWriter
    {
        /// <summary>update_engine marks an absent block run with an all-ones start block.</summary>
        const ulong SparseHole = ulong.MaxValue;

        /// <summary>
        /// Byte offset just past <paramref name="extent"/>. Extents come from the manifest, which
        /// is untrusted, so a run that overflows a 64-bit offset is a format error.
        /// </summary>
        static long EndOf(Extent extent, uint blockSize)
        {
            try
            {
                checked
                {
                    ulong endBlock = extent.StartBlock + extent.NumBlocks;
                    return (long)(endBlock * blockSize);
                }
            }
            catch (OverflowException)
            {
                throw new PayloadFormatException(
                    "an extent at block " + extent.StartBlock + " with " + extent.NumBlocks + " blocks is out of range");
            }
        }

        internal static long TotalBytes(IList<Extent> extents, uint blockSize)
        {
            long total = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].StartBlock == SparseHole)
                    continue;
                try
                {
                    total = checked(total + (long)(extents[i].NumBlocks * blockSize));
                }
                catch (OverflowException)
                {
                    throw new PayloadFormatException("an operation's extents add up to more than 2^63 bytes");
                }
            }
            return total;
        }

        internal static long HighestByteOffset(IList<Extent> extents, uint blockSize)
        {
            long end = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].StartBlock == SparseHole)
                    continue;
                end = Math.Max(end, EndOf(extents[i], blockSize));
            }
            return end;
        }

        /// <summary>
        /// Throws unless every extent lies inside the first <paramref name="limit"/> bytes, so
        /// a damaged or hostile manifest cannot grow the output file without bound.
        /// </summary>
        internal static void CheckWithin(IList<Extent> extents, uint blockSize, long limit)
        {
            long end = HighestByteOffset(extents, blockSize);
            if (end > limit)
            {
                throw new PayloadFormatException(
                    "an operation writes up to byte " + end + " of a partition that is only " + limit + " bytes");
            }
        }

        /// <summary>
        /// Copies every remaining byte of <paramref name="data"/> into <paramref name="dst"/>
        /// across <paramref name="extents"/>. The caller has already checked that the extents fit
        /// the partition and that the data fills them.
        /// </summary>
        internal static void Write(
            Stream dst, IList<Extent> extents, uint blockSize, MemoryStream data, byte[] copyBuffer)
        {
            long remaining = data.Length - data.Position;

            for (int i = 0; i < extents.Count && remaining > 0; i++)
            {
                Extent extent = extents[i];
                if (extent.StartBlock == SparseHole)
                    continue;

                long capacity = (long)extent.NumBlocks * blockSize;
                long take = Math.Min(capacity, remaining);

                dst.Seek((long)extent.StartBlock * blockSize, SeekOrigin.Begin);

                long left = take;
                while (left > 0)
                {
                    int want = (int)Math.Min(copyBuffer.Length, left);
                    int got = data.Read(copyBuffer, 0, want);
                    if (got <= 0)
                        throw new PayloadFormatException("decoded operation data ended early");
                    dst.Write(copyBuffer, 0, got);
                    left -= got;
                }

                remaining -= take;
            }

            if (remaining > 0)
            {
                throw new PayloadFormatException(
                    "operation produced " + remaining + " bytes more than its destination extents hold");
            }
        }
    }
}
