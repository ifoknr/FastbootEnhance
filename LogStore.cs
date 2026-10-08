using System;
using System.Text;

namespace FastbootEnhance
{
    /// <summary>
    /// The session log. Everything fastboot prints, and what the app did about it, lands here;
    /// the Logs page and the Flash page's live log both listen to it. It replaces the old
    /// pop-up logger, which only recorded while its window happened to be open.
    /// </summary>
    static class LogStore
    {
        const int MaxChars = 2000000;
        const int TrimTo = 1500000;

        static readonly object gate = new object();
        static readonly StringBuilder text = new StringBuilder();

        /// <summary>Raised on the writer's thread with the stamped line.</summary>
        public static event Action<string> LineAdded;

        /// <summary>Raised after <see cref="Clear"/>.</summary>
        public static event Action Cleared;

        public static void Append(string line)
        {
            if (line == null)
                return;

            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            lock (gate)
            {
                text.Append(stamped).Append('\n');
                if (text.Length > MaxChars)
                    text.Remove(0, text.Length - TrimTo);
            }

            Action<string> handler = LineAdded;
            if (handler != null)
                handler(stamped);
        }

        public static string Snapshot()
        {
            lock (gate)
                return text.ToString();
        }

        public static void Clear()
        {
            lock (gate)
                text.Clear();

            Action handler = Cleared;
            if (handler != null)
                handler();
        }
    }
}
