using System;
using System.IO;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Persists the bdSeq of the most recent MQTT connection so the sequence continues across restarts.
    /// Sparkplug 3.0: bdSeq starts at 0, increases by one with every new MQTT CONNECT, and wraps from
    /// 255 to 0 (tck-id-topics-nbirth-bdseq-increment, tck-id-message-flow-edge-node-birth-publish-will-message-payload-bdSeq).
    /// Failures are reported to the caller rather than thrown: a node that cannot persist its bdSeq can
    /// still publish.
    /// </summary>
    internal class BdSeqManager
    {
        private const string ApplicationFolder = "jpi-sparkplug";

        // Not allowed in Windows file names; replaced on every platform so the name is portable.
        private static readonly char[] UnsafeFileNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        private readonly string _filePath;

        /// <param name="configuredPath">
        /// The configured file, or null for the default: one file per edge node in the OS state folder.
        /// A relative path is resolved against the application folder.
        /// </param>
        public BdSeqManager(string? configuredPath, string groupId, string edgeNodeId)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                _filePath = Path.Combine(DefaultDirectory(), FileName(groupId, edgeNodeId));
            }
            else
            {
                // Relative to the application folder, not the current directory: a Windows service
                // starts in C:\Windows\System32.
                _filePath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(AppContext.BaseDirectory, configuredPath);
            }
        }

        public string FilePath => _filePath;

        /// <summary>
        /// The bdSeq of the last connection, or null if none is recorded. When the file exists but cannot
        /// be used, returns null and says why in <paramref name="problem"/>.
        /// </summary>
        public ulong? LoadLast(out string? problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(_filePath)) return null;

                var text = File.ReadAllText(_filePath).Trim();
                if (ulong.TryParse(text, out var value)) return value;

                problem = $"it contains '{(text.Length > 20 ? text[..20] + "..." : text)}', not a number";
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            return null;
        }

        /// <summary>Records the bdSeq; returns false and says why if it could not be written.</summary>
        public bool TrySave(ulong value, out string? problem)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(_filePath, value.ToString());
                problem = null;
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// The bdSeq for the next connection: 0 when no connection has been recorded, otherwise one more
        /// than the last, wrapping from 255 to 0.
        /// </summary>
        public static ulong Next(ulong? last) => last is { } value ? unchecked(value + 1) % 256 : 0;

        /// <summary>"&lt;group&gt;.&lt;node&gt;.bdseq", so each edge node keeps its own sequence.</summary>
        internal static string FileName(string groupId, string edgeNodeId) =>
            $"{SafeForFileName(groupId)}.{SafeForFileName(edgeNodeId)}.bdseq";

        internal static string DefaultDirectory() => DefaultDirectory(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        /// <summary>
        /// Windows: %ProgramData%\JPI Solutions\Sparkplug, writable by services. Elsewhere: the directory
        /// systemd's StateDirectory= provides ($STATE_DIRECTORY, the first if it lists several), else
        /// $XDG_STATE_HOME/jpi-sparkplug, else ~/.local/state/jpi-sparkplug.
        /// </summary>
        internal static string DefaultDirectory(bool isWindows, Func<string, string?> environment, string commonApplicationData, string userHome)
        {
            if (isWindows)
            {
                return Path.Combine(commonApplicationData, "JPI Solutions", "Sparkplug");
            }

            var stateDirectory = environment("STATE_DIRECTORY");
            if (!string.IsNullOrEmpty(stateDirectory))
            {
                return stateDirectory.Split(':')[0];
            }

            var xdgStateHome = environment("XDG_STATE_HOME");
            if (!string.IsNullOrEmpty(xdgStateHome))
            {
                return Path.Combine(xdgStateHome, ApplicationFolder);
            }

            return Path.Combine(userHome, ".local", "state", ApplicationFolder);
        }

        private static string SafeForFileName(string name) =>
            new(name.Select(c => c < ' ' || Array.IndexOf(UnsafeFileNameChars, c) >= 0 ? '_' : c).ToArray());
    }
}
