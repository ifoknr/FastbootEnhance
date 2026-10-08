using System;
using System.IO;
using System.IO.Compression;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// Where a payload's bytes live, and how to get an independent reader over them.
    /// Each extraction worker opens its own stream, so partitions can be decoded in parallel.
    /// </summary>
    public sealed class PayloadSource : IDisposable
    {
        const string PayloadEntryName = "payload.bin";
        const int ReadBufferSize = 1 << 18;

        readonly string backingFile;
        readonly long dataOffset;
        readonly long dataLength;
        readonly string fileToDeleteOnDispose;

        PayloadSource(
            string containerPath,
            string backingFile,
            long dataOffset,
            long dataLength,
            bool fromZip,
            bool readInPlace,
            string fileToDeleteOnDispose)
        {
            ContainerPath = containerPath;
            this.backingFile = backingFile;
            this.dataOffset = dataOffset;
            this.dataLength = dataLength;
            FromZip = fromZip;
            ReadInPlace = readInPlace;
            this.fileToDeleteOnDispose = fileToDeleteOnDispose;
        }

        /// <summary>The path the user picked: a payload.bin or an OTA zip.</summary>
        public string ContainerPath { get; }

        /// <summary>True when the payload came from inside a zip.</summary>
        public bool FromZip { get; }

        /// <summary>
        /// True when the payload is read straight out of the zip with no unpacking step.
        /// False means the entry was deflated and had to be inflated to a temporary file.
        /// </summary>
        public bool ReadInPlace { get; }

        public long Length => dataLength;

        /// <summary>
        /// Opens <paramref name="path"/>, which may be a raw payload.bin or an OTA zip.
        /// A deflated payload.bin inside a zip needs <paramref name="temporaryDirectory"/>.
        /// </summary>
        public static PayloadSource Open(string path, string temporaryDirectory = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("payload not found", path);

            byte[] magic = new byte[4];
            using (FileStream probe = File.OpenRead(path))
            {
                int got = probe.Read(magic, 0, 4);
                if (got < 4)
                    throw new PayloadFormatException("file is too small to be a payload");
            }

            if (magic[0] == (byte)'C' && magic[1] == (byte)'r' && magic[2] == (byte)'A' && magic[3] == (byte)'U')
            {
                long length = new FileInfo(path).Length;
                return new PayloadSource(path, path, 0, length, false, true, null);
            }

            bool looksLikeZip = magic[0] == 0x50 && magic[1] == 0x4b;
            if (!looksLikeZip)
            {
                throw new PayloadFormatException(
                    "unrecognised file: expected a payload.bin starting with \"CrAU\" or an OTA zip");
            }

            ZipEntryLocation? found;
            using (FileStream zip = File.OpenRead(path))
            {
                found = ZipEntryLocator.Find(zip, PayloadEntryName);
            }

            if (found == null)
                throw new PayloadFormatException("this zip has no " + PayloadEntryName + " entry");

            ZipEntryLocation entry = found.Value;
            if (entry.IsStored)
                return new PayloadSource(path, path, entry.DataOffset, entry.CompressedSize, true, true, null);

            if (string.IsNullOrEmpty(temporaryDirectory))
            {
                throw new PayloadFormatException(
                    PayloadEntryName + " is deflated in this package, so it needs a temporary directory to unpack into");
            }

            string unpacked = InflateEntry(path, temporaryDirectory);
            long unpackedLength = new FileInfo(unpacked).Length;
            return new PayloadSource(path, unpacked, 0, unpackedLength, true, false, unpacked);
        }

        static string InflateEntry(string zipPath, string temporaryDirectory)
        {
            Directory.CreateDirectory(temporaryDirectory);
            string target = Path.Combine(
                temporaryDirectory, PayloadEntryName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (FileStream file = File.OpenRead(zipPath))
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry entry = archive.GetEntry(PayloadEntryName);
                    if (entry == null)
                        throw new PayloadFormatException("this zip has no " + PayloadEntryName + " entry");

                    using (Stream source = entry.Open())
                    using (FileStream destination = new FileStream(
                        target, FileMode.Create, FileAccess.Write, FileShare.None, ReadBufferSize))
                    {
                        source.CopyTo(destination, ReadBufferSize);

                        // A damaged deflate stream can end early or run long without the
                        // inflater complaining, so check the result against the zip directory.
                        if (destination.Length != entry.Length)
                        {
                            throw new PayloadFormatException(
                                PayloadEntryName + " unpacked to " + destination.Length + " bytes but the package says "
                                + entry.Length + "; the zip is damaged");
                        }
                    }
                }
            }
            catch
            {
                // A failed unpack (full disk, damaged deflate stream) must not leave gigabytes behind.
                DeleteQuietly(target);
                throw;
            }

            return target;
        }

        /// <summary>
        /// A fresh seekable reader positioned at the start of the payload. The caller disposes it.
        /// </summary>
        public Stream OpenStream()
        {
            FileStream file = new FileStream(
                backingFile, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize,
                FileOptions.RandomAccess);

            if (dataOffset == 0 && dataLength == file.Length)
                return file;

            return new SlicedStream(file, dataOffset, dataLength, true);
        }

        public void Dispose()
        {
            if (fileToDeleteOnDispose != null)
                DeleteQuietly(fileToDeleteOnDispose);
        }

        static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
