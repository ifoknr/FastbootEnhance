using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FastbootEnhance.Core;
using FastbootEnhance.Core.Images;

namespace FastbootEnhance.PayloadTool
{
    /// <summary>The image tools (sparse, super) on the command line.</summary>
    static class ImageCommands
    {
        public static void Usage()
        {
            Console.WriteLine("  imginfo  <image> [more parts...]");
            Console.WriteLine("  simg2img <output.img> <sparse.img> [more parts...]");
            Console.WriteLine("  img2simg <input.img> <output.img> [--block N] [--split BYTES]");
            Console.WriteLine("  lpunpack <super.img> <dir> [-p name ...] [--slot N]");
            Console.WriteLine("  mksuper  <output.img> <size> <name=image> [...]   (test images)");
        }

        public static int ImgInfo(string[] args)
        {
            if (args.Length == 0)
                return Fail("imginfo needs an image");
            IList<string> parts = args.Length > 1 ? args : SparseConverter.FindParts(args[0]);

            ImageInfo info = ImageProbe.Identify(parts[0]);
            Console.WriteLine("file            " + parts[0]);
            Console.WriteLine("sparse          " + (info.IsSparse ? "yes" : "no"));
            Console.WriteLine("content         " + info.Kind);
            Console.WriteLine("image size      " + ByteSize.Format(info.ImageLength) + " (" + info.ImageLength + " bytes)");

            if (info.IsSparse)
            {
                foreach (string part in parts)
                {
                    SparseImage image = SparseImage.Open(part);
                    Console.WriteLine();
                    Console.WriteLine("part            " + Path.GetFileName(part));
                    Console.WriteLine("  version       " + image.MajorVersion + "." + image.MinorVersion);
                    Console.WriteLine("  block size    " + image.BlockSize);
                    Console.WriteLine("  blocks        " + image.TotalBlocks);
                    Console.WriteLine("  chunks        " + image.Chunks.Count);
                    Console.WriteLine("  raw           " + image.CountBlocks(SparseChunkType.Raw) + " blocks");
                    Console.WriteLine("  fill          " + image.CountBlocks(SparseChunkType.Fill) + " blocks");
                    Console.WriteLine("  don't care    " + image.CountBlocks(SparseChunkType.DontCare) + " blocks");
                    Console.WriteLine("  checksum      " + (image.ImageChecksum == 0 ? "none" : image.ImageChecksum.ToString("x8")));
                }
            }

            if (info.Kind == ImageKind.Super)
            {
                using (Stream stream = Open(parts))
                {
                    SuperImage super = SuperImage.Read(stream);
                    Console.WriteLine();
                    Console.WriteLine("metadata        " + super.MajorVersion + "." + super.MinorVersion
                        + ", " + super.MetadataSlotCount + " slots" + (super.IsVirtualAB ? ", virtual A/B" : "")
                        + (super.UsedBackup ? " (backup copy)" : ""));
                    foreach (SuperGroup group in super.Groups)
                        Console.WriteLine("group           " + group.Name + "  max " + (group.MaximumSize == 0 ? "unlimited" : ByteSize.Format((long)group.MaximumSize)));
                    foreach (SuperPartition p in super.Partitions)
                    {
                        Console.WriteLine("partition       " + p.Name.PadRight(22) + ByteSize.Format(p.Size).PadLeft(11)
                            + "  " + p.Group + "  " + p.Extents.Count + " extent(s)" + (p.ReadOnly ? "  readonly" : ""));
                    }
                }
            }
            return 0;
        }

        public static int Simg2Img(string[] args)
        {
            if (args.Length < 2)
                return Fail("simg2img needs an output and at least one sparse image");
            string output = args[0];
            List<string> parts = args.Skip(1).ToList();
            if (parts.Count == 1)
                parts = SparseConverter.FindParts(parts[0]).ToList();
            Stopwatch clock = Stopwatch.StartNew();
            ExpandResult result = SparseConverter.ToRaw(parts, output, null, CancellationToken.None);
            Console.WriteLine("wrote " + output + ": " + ByteSize.Format(result.Bytes) + " from " + parts.Count + " part(s), crc32 "
                + result.Crc32.ToString("x8") + ", " + result.ChecksumsChecked + " checksum(s) verified, "
                + clock.Elapsed.TotalSeconds.ToString("F2") + " s");
            return 0;
        }

        public static int Img2Simg(string[] args)
        {
            if (args.Length < 2)
                return Fail("img2simg needs an input and an output");
            int block = 4096;
            long? split = null;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--block" && i + 1 < args.Length) block = int.Parse(args[++i]);
                else if (args[i] == "--split" && i + 1 < args.Length) split = long.Parse(args[++i]);
                else return Fail("unknown option " + args[i]);
            }
            Stopwatch clock = Stopwatch.StartNew();
            SparseWriteResult result = SparseConverter.ToSparse(args[0], args[1], block, split, null, CancellationToken.None);
            Console.WriteLine("wrote " + result.Files.Count + " file(s), " + result.Chunks + " chunks, "
                + ByteSize.Format(result.Bytes) + " for " + result.Blocks + " blocks, "
                + clock.Elapsed.TotalSeconds.ToString("F2") + " s");
            foreach (string file in result.Files)
                Console.WriteLine("  " + file + "  " + new FileInfo(file).Length);
            return 0;
        }

        public static int LpUnpack(string[] args)
        {
            if (args.Length < 2)
                return Fail("lpunpack needs a super image and an output folder");
            int slot = 0;
            HashSet<string> only = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--slot" && i + 1 < args.Length) slot = int.Parse(args[++i]);
                else if (args[i] == "-p" && i + 1 < args.Length) only.Add(args[++i]);
                else return Fail("unknown option " + args[i]);
            }

            Directory.CreateDirectory(args[1]);
            using (Stream stream = Open(SparseConverter.FindParts(args[0])))
            {
                SuperImage super = SuperImage.Read(stream, slot);
                foreach (SuperPartition p in super.Partitions)
                {
                    if (only.Count > 0 ? !only.Contains(p.Name) : p.IsEmpty)
                        continue;
                    string path = Path.Combine(args[1], p.Name + ".img");
                    using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        long bytes = SuperImage.Extract(stream, p, file, null, CancellationToken.None);
                        Console.WriteLine(p.Name.PadRight(22) + ByteSize.Format(bytes).PadLeft(11) + "  " + path);
                    }
                }
            }
            return 0;
        }

        public static int MkSuper(string[] args)
        {
            if (args.Length < 3)
                return Fail("mksuper needs an output, a size and name=image pairs");
            SuperBuilder builder = new SuperBuilder();
            builder.AddGroup("main", 0);
            List<Stream> opened = new List<Stream>();
            try
            {
                foreach (string pair in args.Skip(2))
                {
                    string[] kv = pair.Split(new[] { '=' }, 2);
                    Stream data = kv.Length == 2 && kv[1].Length > 0 ? File.OpenRead(kv[1]) : null;
                    if (data != null)
                        opened.Add(data);
                    builder.AddPartition(kv[0], "main", data);
                }
                using (FileStream output = new FileStream(args[0], FileMode.Create, FileAccess.ReadWrite))
                    builder.Write(output, long.Parse(args[1]));
            }
            finally
            {
                foreach (Stream s in opened)
                    s.Dispose();
            }
            Console.WriteLine("wrote " + args[0]);
            return 0;
        }

        static Stream Open(IList<string> parts)
        {
            if (SparseImage.IsSparse(parts[0]))
                return SparseStream.Open(parts);
            return new FileStream(parts[0], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }
    }
}
