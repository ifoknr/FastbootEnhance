using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace FastbootEnhance.Core.Adb
{
    /// <summary>Copies a stream to a file while hashing it, so a backup is read only once.</summary>
    public static class HashingCopy
    {
        public sealed class Result
        {
            public Result(long bytes, string sha256)
            {
                Bytes = bytes;
                Sha256 = sha256;
            }

            public long Bytes { get; }
            public string Sha256 { get; }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/> until the source
        /// ends, reporting the running byte count. Stops early on cancellation.
        /// </summary>
        public static Result Copy(Stream source, Stream destination, IProgress<long> progress,
            CancellationToken cancellation)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 20];
                long total = 0;
                long lastReport = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    total += read;
                    if (progress != null && total - lastReport >= (4 << 20))
                    {
                        lastReport = total;
                        progress.Report(total);
                    }
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                if (progress != null)
                    progress.Report(total);
                return new Result(total, Sha256Sums.Hex(sha.Hash));
            }
        }
    }
}
