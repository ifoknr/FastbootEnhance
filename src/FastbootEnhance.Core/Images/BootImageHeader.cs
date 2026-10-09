using System;
using System.IO;
using System.Text;

namespace FastbootEnhance.Core.Images
{
    public enum BootImageKind
    {
        /// <summary>Not an Android boot image at all.</summary>
        NotBootImage,

        /// <summary>A boot or recovery image with a kernel: it can be started with "fastboot boot".</summary>
        Bootable,

        /// <summary>An ANDROID! image without a kernel, like init_boot: nothing to start on its own.</summary>
        NoKernel,

        /// <summary>A vendor_boot image (VNDRBOOT): ramdisks and DTB only.</summary>
        VendorBoot,
    }

    /// <summary>The fields of an Android boot image header that say what the image can do.</summary>
    public sealed class BootImageHeader
    {
        BootImageHeader()
        {
        }

        public BootImageKind Kind { get; private set; }

        /// <summary>Header version (0 to 4); -1 when not a boot image.</summary>
        public int HeaderVersion { get; private set; } = -1;

        public long KernelSize { get; private set; }
        public long RamdiskSize { get; private set; }

        public static BootImageHeader Read(string path)
        {
            using (FileStream file = File.OpenRead(path))
                return Read(file);
        }

        /// <summary>
        /// Reads the header. The kernel size is at byte 8 and the header version at byte 40 in
        /// every version (0 to 4) of the boot image header, and the ramdisk size at 16 (v0-v2)
        /// or 12 (v3, v4).
        /// </summary>
        public static BootImageHeader Read(Stream stream)
        {
            BootImageHeader header = new BootImageHeader { Kind = BootImageKind.NotBootImage };
            byte[] head = new byte[48];
            stream.Position = 0;
            if (!StreamUtil.TryReadExactly(stream, head))
                return header;

            string magic = Encoding.ASCII.GetString(head, 0, 8);
            if (magic == "VNDRBOOT")
            {
                header.Kind = BootImageKind.VendorBoot;
                header.HeaderVersion = (int)BitConverter.ToUInt32(head, 8);
                return header;
            }
            if (magic != "ANDROID!")
                return header;

            uint version = BitConverter.ToUInt32(head, 40);
            header.HeaderVersion = version <= 16 ? (int)version : 0;
            header.KernelSize = BitConverter.ToUInt32(head, 8);
            header.RamdiskSize = BitConverter.ToUInt32(head, header.HeaderVersion >= 3 ? 12 : 16);
            header.Kind = header.KernelSize > 0 ? BootImageKind.Bootable : BootImageKind.NoKernel;
            return header;
        }
    }
}
