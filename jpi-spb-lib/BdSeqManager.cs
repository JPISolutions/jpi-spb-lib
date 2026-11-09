using System;
using System.IO;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Persists the bdSeq of the most recent MQTT connection so the sequence continues across restarts.
    /// Sparkplug 3.0: bdSeq starts at 0, increases by one with every new MQTT CONNECT, and wraps from
    /// 255 to 0 (tck-id-topics-nbirth-bdseq-increment, tck-id-message-flow-edge-node-birth-publish-will-message-payload-bdSeq).
    /// </summary>
    internal class BdSeqManager
    {
        internal const string DefaultFileName = "sparkplug_bdseq.txt";

        private readonly string _filePath;

        public BdSeqManager(string? filePath = null)
        {
            var path = string.IsNullOrWhiteSpace(filePath) ? DefaultFileName : filePath;

            // Resolve a relative name against the application folder, not the current directory:
            // a Windows service starts in C:\Windows\System32.
            _filePath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        }

        public string FilePath => _filePath;

        /// <summary>The bdSeq of the last connection, or null if none has been recorded.</summary>
        public ulong? LoadLast()
        {
            try
            {
                if (File.Exists(_filePath) && ulong.TryParse(File.ReadAllText(_filePath).Trim(), out var value))
                {
                    return value;
                }
            }
            catch
            {
                // Unreadable state: start the sequence over
            }

            return null;
        }

        public void Save(ulong value)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_filePath, value.ToString());
            }
            catch
            {
                // Log if needed
            }
        }

        /// <summary>
        /// The bdSeq for the next connection: 0 when no connection has been recorded, otherwise one more
        /// than the last, wrapping from 255 to 0.
        /// </summary>
        public static ulong Next(ulong? last) => last is { } value ? unchecked(value + 1) % 256 : 0;
    }
}
