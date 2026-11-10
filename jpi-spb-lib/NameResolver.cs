using Serilog;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Applies <see cref="SparkplugNames"/> to the device IDs and metric names callers publish. With
    /// renaming on, an invalid name is published under its valid form; with renaming off, it is dropped
    /// as if it were not in the set. Either way it is logged once. Two different names that would be
    /// published under the same name are a collision: the first keeps it, later ones are dropped.
    /// </summary>
    internal sealed class NameResolver
    {
        private readonly bool _rename;
        private readonly Func<ILogger> _logger;
        private readonly object _gate = new();

        // Published name -> the caller's name that owns it.
        private readonly Dictionary<string, string> _deviceOwners = new();
        private readonly Dictionary<(string? Device, string Metric), string> _metricOwners = new();
        private readonly HashSet<string> _reported = new();

        public NameResolver(bool rename, Func<ILogger> logger)
        {
            _rename = rename;
            _logger = logger;
        }

        /// <summary>The device ID to publish under, or null if the device is not published.</summary>
        public string? ResolveDeviceId(string deviceId)
        {
            lock (_gate)
            {
                var published = Resolve("Device ID", deviceId, SparkplugNames.IsValidId(deviceId),
                    () => SparkplugNames.MakeValidId(deviceId));
                if (published is null) return null;

                return Claim(_deviceOwners, published, published, deviceId, "Device ID") ? published : null;
            }
        }

        /// <summary>
        /// Copies of the metrics under the names to publish (device null for node metrics), leaving out
        /// any that are not published. The caller's objects are not changed.
        /// </summary>
        public List<Metric> ResolveMetrics(string? device, IEnumerable<Metric> metrics)
        {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));

            var resolved = new List<Metric>();
            lock (_gate)
            {
                foreach (var metric in metrics)
                {
                    if (metric == null) throw new ArgumentException("Metrics cannot contain null", nameof(metrics));

                    var what = device is null ? "Node metric" : $"Metric of device '{device}'";
                    var published = Resolve(what, metric.Name, SparkplugNames.IsValidMetricName(metric.Name),
                        () => SparkplugNames.MakeValidMetricName(metric.Name));
                    if (published is null) continue;

                    if (!Claim(_metricOwners, (device, published), published, metric.Name, what)) continue;

                    if (published == metric.Name)
                    {
                        resolved.Add(metric);
                    }
                    else
                    {
                        var copy = metric.Clone();
                        copy.Name = published;
                        resolved.Add(copy);
                    }
                }
            }

            return resolved;
        }

        private string? Resolve(string what, string name, bool valid, Func<string> makeValid)
        {
            if (valid) return name;

            if (!_rename)
            {
                ReportOnce($"invalid|{what}|{name}", logger => logger.Error(
                    "{What} '{Name}' is not a valid Ignition name (letters, digits, '_', space and ' - : ( ) only, " +
                    "starting with a letter, digit or '_'); it is not published", what, name));
                return null;
            }

            var published = makeValid();
            ReportOnce($"renamed|{what}|{name}", logger => logger.Warning(
                "{What} '{Name}' is not a valid Ignition name; publishing it as '{Published}'", what, name, published));
            return published;
        }

        private bool Claim<TKey>(Dictionary<TKey, string> owners, TKey key, string published, string name, string what) where TKey : notnull
        {
            if (owners.TryGetValue(key, out var owner) && owner != name)
            {
                ReportOnce($"collision|{what}|{name}", logger => logger.Error(
                    "{What} '{Name}' would be published as '{Published}', which '{Owner}' already uses; it is not published",
                    what, name, published, owner));
                return false;
            }

            owners[key] = name;
            return true;
        }

        private void ReportOnce(string key, Action<ILogger> log)
        {
            if (_reported.Add(key)) log(_logger());
        }
    }
}
