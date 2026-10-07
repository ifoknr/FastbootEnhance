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

        public FlashRow(string name, string size, string codec)
        {
            Name = name;
            Size = size;
            Codec = codec;
        }

        public string Name { get; }
        public string Size { get; }
        public string Codec { get; }

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
}
