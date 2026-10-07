using System.IO;
using FastbootEnhance.Core.Payload;

namespace FastbootEnhance.Core
{
    internal static class StreamUtil
    {
        /// <summary>
        /// Fills <paramref name="buffer"/> from <paramref name="offset"/> with exactly
        /// <paramref name="count"/> bytes, or throws <paramref name="whenShort"/>'s message.
        /// </summary>
        internal static void ReadExactly(Stream stream, byte[] buffer, int offset, int count, string whenShort)
        {
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, offset + read, count - read);
                if (got <= 0)
                    throw new PayloadFormatException(whenShort);
                read += got;
            }
        }

        internal static byte[] ReadExactly(Stream stream, int count, string whenShort)
        {
            byte[] buffer = new byte[count];
            ReadExactly(stream, buffer, 0, count, whenShort);
            return buffer;
        }
    }

    public static class ByteSize
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>"512 B", "64.00 MB", "1.24 GB"; a negative size reads as "-".</summary>
        public static string Format(long bytes)
        {
            if (bytes < 0)
                return "-";

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return (unit == 0 ? value.ToString("F0") : value.ToString("F2")) + " " + Units[unit];
        }
    }
}
