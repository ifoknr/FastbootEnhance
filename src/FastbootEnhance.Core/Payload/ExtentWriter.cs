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

        internal static long TotalBytes(IList<Extent> extents, uint blockSize)
        {
            long total = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                if (extents[i].StartBlock == SparseHole)
                    continue;
                total += (long)extents[i].NumBlocks * blockSize;
            }
            return total;
        }

        internal static long HighestByteOffset(IList<Extent> extents, uint blockSize)
        {
            long end = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                Extent e = extents[i];
                if (e.StartBlock == SparseHole)
                    continue;
                long extentEnd = (long)(e.StartBlock + e.NumBlocks) * blockSize;
                if (extentEnd > end)
                    end = extentEnd;
            }
            return end;
        }

        /// <summary>
        /// Copies every remaining byte of <paramref name="data"/> into <paramref name="dst"/>
        /// across <paramref name="extents"/>. Data shorter than the extents leaves the tail
        /// untouched (the output file is zero-filled); data longer than them is an error.
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
