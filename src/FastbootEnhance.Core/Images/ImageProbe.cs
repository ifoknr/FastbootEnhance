using System;
using System.IO;
using System.Text;

namespace FastbootEnhance.Core.Images
{
    public enum ImageKind
    {
        Unknown,
        Super,
        Ext4,
        Erofs,
        F2fs,
        BootImage,
        VendorBootImage,
        Vbmeta,
        Dtbo,
        Payload,
        Zip,
    }

    /// <summary>What a file is: sparse or not, and what the (expanded) image holds.</summary>
    public sealed class ImageInfo
    {
        public string Path { get; internal set; }
        public bool IsSparse { get; internal set; }
        public ImageKind Kind { get; internal set; }

        /// <summary>Size of the image once expanded (the file size when it is not sparse).</summary>
        public long ImageLength { get; internal set; }
    }

    /// <summary>Recognises the images found in Android firmware by their magic numbers.</summary>
    public static class ImageProbe
    {
        public static ImageInfo Identify(string path)
        {
            ImageInfo info = new ImageInfo { Path = path };
            using (FileStream file = File.OpenRead(path))
            {
                info.IsSparse = SparseImage.IsSparse(file);
                if (!info.IsSparse)
                {
                    info.ImageLength = file.Length;
                    info.Kind = Kind(file);
                    return info;
                }
            }

            using (SparseStream expanded = SparseStream.Open(SparseConverter.FindParts(path)))
            {
                info.ImageLength = expanded.Length;
                info.Kind = Kind(expanded);
            }
            return info;
        }

        /// <summary>Identifies the content of an (expanded) image stream.</summary>
        public static ImageKind Kind(Stream stream)
        {
            byte[] head = ReadAt(stream, 0, 8);
            if (head != null)
            {
                string ascii = Encoding.ASCII.GetString(head);
                if (ascii == "ANDROID!") return ImageKind.BootImage;
                if (ascii == "VNDRBOOT") return ImageKind.VendorBootImage;
                if (ascii.StartsWith("AVB0", StringComparison.Ordinal)) return ImageKind.Vbmeta;
                if (ascii.StartsWith("CrAU", StringComparison.Ordinal)) return ImageKind.Payload;
                if (head[0] == 0x50 && head[1] == 0x4B && head[2] == 3 && head[3] == 4) return ImageKind.Zip;
                if (head[0] == 0xD7 && head[1] == 0xB7 && head[2] == 0xAB && head[3] == 0x1E) return ImageKind.Dtbo;
            }

            if (SuperImage.IsSuper(stream)) return ImageKind.Super;

            byte[] erofs = ReadAt(stream, 1024, 4);
            if (erofs != null && BitConverter.ToUInt32(erofs, 0) == 0xE0F5E1E2) return ImageKind.Erofs;
            if (erofs != null && BitConverter.ToUInt32(erofs, 0) == 0xF2F52010) return ImageKind.F2fs;

            byte[] ext4 = ReadAt(stream, 1024 + 56, 2);
            if (ext4 != null && BitConverter.ToUInt16(ext4, 0) == 0xEF53) return ImageKind.Ext4;

            return ImageKind.Unknown;
        }

        static byte[] ReadAt(Stream stream, long offset, int count)
        {
            if (stream.Length < offset + count)
                return null;
            stream.Position = offset;
            byte[] data = new byte[count];
            return StreamUtil.TryReadExactly(stream, data) ? data : null;
        }
    }
}
