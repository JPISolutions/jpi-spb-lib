using System.Text;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// The naming rules this library enforces: Ignition's rules for tag and folder names (8.1 and 8.3).
    /// The first character must be a letter, a digit or '_'; every later character a letter, a digit,
    /// '_', a space, or one of <c>' - : ( )</c>.
    /// </summary>
    /// <remarks>
    /// Group, edge node and device IDs become folders in an Ignition host's tag tree, and each
    /// '/'-separated part of a metric name becomes a folder or a tag. A '.' in a Group ID is known to stop
    /// MQTT Engine ingesting a node's data. The rules also exclude the characters Sparkplug reserves in IDs,
    /// '+', '/' and '#' (tck-id-topic-structure-namespace-valid-group-id, -edge-node-id, -device-id).
    /// "Letter" and "digit" are Unicode letters and decimal digits, so names such as "Température" are valid.
    /// </remarks>
    public static class SparkplugNames
    {
        /// <summary>True if the name is valid as a group, edge node or device ID (one Ignition folder name).</summary>
        public static bool IsValidId(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            var first = true;
            foreach (var rune in name.EnumerateRunes())
            {
                if (!(first ? IsAllowedFirst(rune) : IsAllowedLater(rune))) return false;
                first = false;
            }

            return true;
        }

        /// <summary>
        /// True if the metric name is valid: one or more '/'-separated parts, each a valid name.
        /// </summary>
        public static bool IsValidMetricName(string? name) =>
            !string.IsNullOrEmpty(name) && name.Split('/').All(IsValidId);

        /// <summary>
        /// Turns a name into a valid ID: every character the rules do not allow becomes '_', and a name
        /// that starts with a character allowed only later (a space, ' - : ( or )) gets a '_' prefix, as
        /// Ignition itself does. A valid name is returned unchanged.
        /// </summary>
        public static string MakeValidId(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "_";

            var result = new StringBuilder(name.Length + 1);
            var first = true;
            foreach (var rune in name.EnumerateRunes())
            {
                if (first && !IsAllowedFirst(rune))
                {
                    result.Append('_');
                    if (IsAllowedLater(rune)) result.Append(rune.ToString());
                }
                else
                {
                    result.Append(IsAllowedLater(rune) ? rune.ToString() : "_");
                }
                first = false;
            }

            return result.ToString();
        }

        /// <summary>
        /// Turns a metric name into a valid one: each '/'-separated part is made valid with
        /// <see cref="MakeValidId"/>, and empty parts ("a//b", a leading or trailing '/') are removed.
        /// A valid name is returned unchanged.
        /// </summary>
        public static string MakeValidMetricName(string? name)
        {
            var parts = (name ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(MakeValidId)
                .ToArray();

            return parts.Length == 0 ? "_" : string.Join('/', parts);
        }

        private static bool IsAllowedFirst(Rune rune) =>
            Rune.IsLetter(rune) || Rune.IsDigit(rune) || rune.Value == '_';

        private static bool IsAllowedLater(Rune rune) =>
            IsAllowedFirst(rune) || rune.Value is ' ' or '\'' or '-' or ':' or '(' or ')';
    }
}
