using System;

namespace FastbootEnhance.Core.Payload
{
    /// <summary>The payload could not be parsed, or its contents contradict the format.</summary>
    public class PayloadFormatException : Exception
    {
        public PayloadFormatException(string message) : base(message) { }
        public PayloadFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A partition could not be extracted. <see cref="Partition"/> names which one.</summary>
    public class PayloadExtractionException : Exception
    {
        public string Partition { get; }

        public PayloadExtractionException(string partition, string message) : base(message)
        {
            Partition = partition;
        }

        public PayloadExtractionException(string partition, string message, Exception inner)
            : base(message, inner)
        {
            Partition = partition;
        }
    }
}
