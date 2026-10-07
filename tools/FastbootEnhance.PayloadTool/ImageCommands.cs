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
            Console.WriteLine("  mksuper  <output.img> --size N [--mode vab|ab|single] [--group main] [--sparse]");
            Console.WriteLine("           [--from super.img] [--folder dir] [image | name=image ...]");
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
            if (args.Length < 2)
                return Fail("mksuper needs an output and images");

            string output = args[0];
            long size = 0;
            SuperSlotMode mode = SuperSlotMode.VirtualAB;
            string group = "main";
            bool sparse = false;
            string from = null;
            List<string> folders = new List<string>();
            List<string> images = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--size" && i + 1 < args.Length) size = long.Parse(args[++i]);
                else if (args[i] == "--mode" && i + 1 < args.Length)
                {
                    string m = args[++i];
                    mode = m == "ab" ? SuperSlotMode.AB : m == "single" ? SuperSlotMode.Single : SuperSlotMode.VirtualAB;
                }
                else if (args[i] == "--group" && i + 1 < args.Length) group = args[++i];
                else if (args[i] == "--sparse") sparse = true;
                else if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--folder" && i + 1 < args.Length) folders.Add(args[++i]);
                else if (args[i].StartsWith("--")) return Fail("unknown option " + args[i]);
                else images.Add(args[i]);
            }

            SuperPlan plan;
            if (from != null)
            {
                using (Stream stream = Open(SparseConverter.FindParts(from)))
                    plan = SuperPlan.FromImage(SuperImage.Read(stream));
                if (size > 0)
                    plan.DeviceSize = size;
            }
            else
            {
                plan = SuperPlan.Create(mode, size, group);
            }
            foreach (string folder in folders)
                Console.WriteLine("found " + plan.AttachFolder(folder) + " images in " + folder);
            foreach (string image in images)
            {
                string[] kv = image.Split(new[] { '=' }, 2);
                SuperPlanPartition added = kv.Length == 2 ? plan.AddImage(kv[1], kv[0]) : plan.AddImage(kv[0]);
                Console.WriteLine("added " + added.Name + " (" + added.Group + ")");
            }

            SuperPlanCheck check = plan.Check();
            Console.WriteLine("super " + plan.DeviceSize + " bytes, slots " + plan.MetadataSlots + ", flags " + plan.HeaderFlags
                              + ", uses " + check.UsedEnd + " bytes");
            if (!check.CanBuild)
                return Fail("cannot build: " + string.Join("; ", check.Problems));

            SuperLayout layout = plan.Build(output, sparse, null, CancellationToken.None);
            foreach (SuperPlacement place in layout.Placements)
                Console.WriteLine(place.Name.PadRight(22) + " at " + place.Offset + ", " + place.Allocated + " bytes");
            SuperVerifyResult verify = plan.Verify(output, null, CancellationToken.None);
            foreach (KeyValuePair<string, string> hash in verify.Sha256)
                Console.WriteLine(hash.Value + "  " + hash.Key);
            if (!verify.Ok)
                return Fail("verification failed: " + string.Join("; ", verify.Problems));
            Console.WriteLine("wrote " + output + (sparse ? " (sparse)" : "") + ", verified");
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
