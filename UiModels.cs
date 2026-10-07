using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace FastbootEnhance
{
    static class Palette
    {
        public static Brush Get(string key)
        {
            return (Brush)Application.Current.FindResource(key);
        }
    }

    /// <summary>One line in the pre-flash checks list.</summary>
    sealed class CheckRow
    {
        public enum Level { Ok, Warn, Danger }

        public string Text { get; }
        public Brush Brush { get; }

        public CheckRow(Level level, string text)
        {
            Text = text;
            Brush = Palette.Get(level == Level.Ok ? "Ok" : level == Level.Warn ? "Warn" : "Danger");
        }
    }

    /// <summary>One partition in the flash queue. Updated from worker threads via the dispatcher.</summary>
    sealed class FlashRow : INotifyPropertyChanged
    {
        public enum Stage { Queued, Extracting, Verified, Flashing, Flashed, Failed, NotWritten }

        double progress;
        Stage stage = Stage.Queued;

        public FlashRow(string name, string size)
        {
            Name = name;
            Size = size;
        }

        public string Name { get; }
        public string Size { get; }

        /// <summary>0 to 100. Extraction fills the first half, writing to the device the second.</summary>
        public double Progress
        {
            get { return progress; }
            set
            {
                if (progress == value)
                    return;
                progress = value;
                Raise(nameof(Progress));
            }
        }

        public Stage Current
        {
            get { return stage; }
            set
            {
                if (stage == value)
                    return;
                stage = value;
                Raise(nameof(Current));
                Raise(nameof(State));
                Raise(nameof(StateBrush));
            }
        }

        public string State
        {
            get
            {
                switch (stage)
                {
                    case Stage.Extracting: return Properties.Resources.flash_state_extracting;
                    case Stage.Verified: return Properties.Resources.flash_state_verified;
                    case Stage.Flashing: return Properties.Resources.flash_state_flashing;
                    case Stage.Flashed: return Properties.Resources.flash_state_flashed;
                    case Stage.Failed: return Properties.Resources.flash_state_failed;
                    case Stage.NotWritten: return Properties.Resources.flash_state_not_written;
                    default: return Properties.Resources.flash_state_queued;
                }
            }
        }

        public Brush StateBrush
        {
            get
            {
                switch (stage)
                {
                    case Stage.Flashed: return Palette.Get("Ok");
                    case Stage.Failed: return Palette.Get("Danger");
                    case Stage.NotWritten: return Palette.Get("Warn");
                    case Stage.Queued: return Palette.Get("Faint");
                    default: return Palette.Get("Accent");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Raise(string property)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>One partition on the Backup page.</summary>
    sealed class BackupRow : INotifyPropertyChanged
    {
        public enum Stage { Idle, Queued, Reading, Saved, Failed, Skipped }

        bool selected;
        double progress;
        Stage stage = Stage.Idle;

        public BackupRow(FastbootEnhance.Core.Adb.DevicePartition partition)
        {
            Partition = partition;
        }

        public FastbootEnhance.Core.Adb.DevicePartition Partition { get; }
        public string Name => Partition.Name;
        public string Size => Partition.Size >= 0 ? Helper.byte2AUnit(Partition.Size) : "?";

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                    return;
                selected = value;
                Raise(nameof(Selected));
            }
        }

        public double Progress
        {
            get { return progress; }
            set
            {
                if (progress == value)
                    return;
                progress = value;
                Raise(nameof(Progress));
            }
        }

        public Stage Current
        {
            get { return stage; }
            set
            {
                if (stage == value)
                    return;
                stage = value;
                Raise(nameof(Current));
                Raise(nameof(State));
                Raise(nameof(StateBrush));
            }
        }

        public string State
        {
            get
            {
                switch (stage)
                {
                    case Stage.Queued: return Properties.Resources.flash_state_queued;
                    case Stage.Reading: return Properties.Resources.backup_state_reading;
                    case Stage.Saved: return Properties.Resources.backup_state_saved;
                    case Stage.Failed: return Properties.Resources.flash_state_failed;
                    case Stage.Skipped: return Properties.Resources.backup_state_skipped;
                    default: return Partition.IsCritical ? Properties.Resources.backup_state_critical : "";
                }
            }
        }

        public Brush StateBrush
        {
            get
            {
                switch (stage)
                {
                    case Stage.Saved: return Palette.Get("Ok");
                    case Stage.Failed: return Palette.Get("Danger");
                    case Stage.Skipped: return Palette.Get("Warn");
                    case Stage.Idle: return Palette.Get("Faint");
                    case Stage.Queued: return Palette.Get("Faint");
                    default: return Palette.Get("Accent");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Raise(string property)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>One file or folder in the Files list.</summary>
    sealed class FileRow
    {
        public FileRow(FastbootEnhance.Core.Adb.DeviceEntry entry)
        {
            Entry = entry;
        }

        public FastbootEnhance.Core.Adb.DeviceEntry Entry { get; }
        public string Name => Entry.Name;
        public string Size => Entry.IsFolder ? "" : Helper.byte2AUnit(Entry.Size);
        public string Kind => Entry.IsFolder ? Properties.Resources.files_kind_folder
            : Entry.IsLink ? Properties.Resources.files_kind_link
            : Properties.Resources.files_kind_file;

        /// <summary>A folder glyph or a page glyph, from the theme's icon set.</summary>
        public Geometry Icon => (Geometry)Application.Current.FindResource(Entry.IsFolder ? "IconFolder" : "IconFile");
        public Brush IconBrush => Palette.Get(Entry.IsFolder ? "Accent" : "Dim");
    }

    /// <summary>A name and a value, for the details lists.</summary>
    sealed class InfoRow
    {
        public InfoRow(string name, string value)
        {
            Name = name;
            Value = Helper.ltr(value);
        }

        public string Name { get; }
        public string Value { get; }
    }

    /// <summary>One chunk of a sparse image, for the Chunks list.</summary>
    sealed class ChunkRow
    {
        public ChunkRow(int index, FastbootEnhance.Core.Images.SparseChunk chunk, int blockSize)
        {
            Index = index.ToString();
            Type = chunk.Type == FastbootEnhance.Core.Images.SparseChunkType.Raw ? "RAW"
                : chunk.Type == FastbootEnhance.Core.Images.SparseChunkType.Fill ? "FILL 0x" + chunk.Value.ToString("x8")
                : chunk.Type == FastbootEnhance.Core.Images.SparseChunkType.DontCare ? "DONT CARE" : "CRC32";
            Start = chunk.OutputBlock.ToString();
            Blocks = chunk.Blocks.ToString();
            Size = Helper.byte2AUnit(chunk.Blocks * blockSize);
        }

        public string Index { get; }
        public string Type { get; }
        public string Start { get; }
        public string Blocks { get; }
        public string Size { get; }
    }

    /// <summary>One logical partition of a super image.</summary>
    sealed class SuperRow : INotifyPropertyChanged
    {
        public enum Stage { Idle, Queued, Extracting, Saved, Failed, Skipped }

        bool selected;
        double progress;
        Stage stage = Stage.Idle;

        public SuperRow(FastbootEnhance.Core.Images.SuperPartition partition)
        {
            Partition = partition;
            selected = CanExtract;
        }

        public FastbootEnhance.Core.Images.SuperPartition Partition { get; }
        public string Name => Partition.Name;
        public string Size => Partition.IsEmpty ? "—" : Helper.byte2AUnit(Partition.Size);

        /// <summary>Empty slots and partitions on other devices cannot be extracted from this image.</summary>
        public bool CanExtract => !Partition.IsEmpty && Partition.OnSuperOnly;

        public string Detail
        {
            get
            {
                switch (stage)
                {
                    case Stage.Queued: return Properties.Resources.flash_state_queued;
                    case Stage.Extracting: return Properties.Resources.flash_state_extracting;
                    case Stage.Saved: return Properties.Resources.backup_state_saved;
                    case Stage.Failed: return Properties.Resources.flash_state_failed;
                    case Stage.Skipped: return Properties.Resources.backup_state_skipped;
                    default:
                        return Partition.IsEmpty ? Properties.Resources.images_empty_slot : Partition.Group;
                }
            }
        }

        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value)
                    return;
                selected = value && CanExtract;
                Raise(nameof(Selected));
            }
        }

        public double Progress
        {
            get { return progress; }
            set
            {
                if (progress == value)
                    return;
                progress = value;
                Raise(nameof(Progress));
            }
        }

        public Stage Current
        {
            get { return stage; }
            set
            {
                if (stage == value)
                    return;
                stage = value;
                Raise(nameof(Current));
                Raise(nameof(Detail));
                Raise(nameof(StateBrush));
            }
        }

        public Brush StateBrush
        {
            get
            {
                switch (stage)
                {
                    case Stage.Saved: return Palette.Get("Ok");
                    case Stage.Failed: return Palette.Get("Danger");
                    case Stage.Skipped: return Palette.Get("Warn");
                    case Stage.Extracting: return Palette.Get("Accent");
                    default: return Palette.Get(Partition.IsEmpty ? "Faint" : "Dim");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Raise(string property)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>One partition of the super image being built.</summary>
    sealed class SuperBuildRow
    {
        public SuperBuildRow(FastbootEnhance.Core.Images.SuperPlanPartition partition)
        {
            Partition = partition;
        }

        public FastbootEnhance.Core.Images.SuperPlanPartition Partition { get; }
        public string Name => Partition.Name;
        public string Group => Partition.Group;
        public string Size => Partition.HasImage ? Helper.byte2AUnit(Partition.Allocated) : "—";

        public string Image => Partition.HasImage
            ? System.IO.Path.GetFileName(Partition.ImagePath) + (Partition.ImageIsSparse ? "  · sparse" : "")
            : Properties.Resources.super_empty_partition;

        public string ImagePath => Partition.ImagePath;
        public Brush NameBrush => Palette.Get(Partition.HasImage ? "Text" : "Faint");
        public Brush ImageBrush => Palette.Get(Partition.HasImage ? "Dim" : "Faint");

        /// <summary>File names read left to right; the "empty" label follows the page.</summary>
        public FlowDirection ImageFlow => Partition.HasImage ? FlowDirection.LeftToRight : Languages.Flow;
    }
}
