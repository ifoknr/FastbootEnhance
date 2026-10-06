using System;
using System.IO;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>
    /// A read-only seekable window over part of another stream, used to read a stored
    /// payload.bin in place inside an OTA zip.
    /// </summary>
    internal sealed class SlicedStream : Stream
    {
        readonly Stream inner;
        readonly long baseOffset;
        readonly long sliceLength;
        readonly bool ownsInner;
        long position;

        internal SlicedStream(Stream inner, long baseOffset, long sliceLength, bool ownsInner)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (!inner.CanSeek) throw new ArgumentException("inner stream must be seekable", nameof(inner));
            if (baseOffset < 0) throw new ArgumentOutOfRangeException(nameof(baseOffset));
            if (sliceLength < 0 || baseOffset + sliceLength > inner.Length)
                throw new ArgumentOutOfRangeException(nameof(sliceLength), "slice runs past the end of the stream");

            this.inner = inner;
            this.baseOffset = baseOffset;
            this.sliceLength = sliceLength;
            this.ownsInner = ownsInner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => sliceLength;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = sliceLength - position;
            if (left <= 0)
                return 0;

            int want = (int)Math.Min(count, left);
            inner.Position = baseOffset + position;
            int got = inner.Read(buffer, offset, want);
            position += got;
            return got;
        }

        public override long Seek(long target, SeekOrigin origin)
        {
            long resolved;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    resolved = target;
                    break;
                case SeekOrigin.Current:
                    resolved = position + target;
                    break;
                case SeekOrigin.End:
                    resolved = sliceLength + target;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin));
            }

            if (resolved < 0)
                throw new IOException("cannot seek before the start of the slice");

            position = resolved;
            return position;
        }

        public override void Flush() { }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsInner)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
