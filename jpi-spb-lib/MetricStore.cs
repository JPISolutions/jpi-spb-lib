using System.Collections.Concurrent;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Stores and tracks metrics for nodes and devices
    /// </summary>
    public class MetricStore
    {
        private readonly ConcurrentDictionary<string, Metric> _nodeMetrics = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Metric>> _deviceMetrics = new();
        private readonly object _lock = new();

        /// <summary>
        /// Updates or adds a node metric
        /// </summary>
        public void UpdateNodeMetric(Metric metric)
        {
            if (metric == null) throw new ArgumentNullException(nameof(metric));

            _nodeMetrics.AddOrUpdate(metric.Name,
                metric,
                (key, existing) =>
                {
                    existing.UpdateValue(metric.Value);
                    return existing;
                });
        }

        /// <summary>
        /// Updates or adds multiple node metrics
        /// </summary>
        public void UpdateNodeMetrics(IEnumerable<Metric> metrics)
        {
            foreach (var metric in metrics)
            {
                UpdateNodeMetric(metric);
            }
        }

        /// <summary>
        /// Updates or adds a device metric
        /// </summary>
        public void UpdateDeviceMetric(string deviceId, Metric metric)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));
            if (metric == null)
                throw new ArgumentNullException(nameof(metric));

            var deviceDict = _deviceMetrics.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, Metric>());

            deviceDict.AddOrUpdate(metric.Name,
                metric,
                (key, existing) =>
                {
                    existing.UpdateValue(metric.Value);
                    return existing;
                });
        }

        /// <summary>
        /// Updates or adds multiple device metrics
        /// </summary>
        public void UpdateDeviceMetrics(string deviceId, IEnumerable<Metric> metrics)
        {
            foreach (var metric in metrics)
            {
                UpdateDeviceMetric(deviceId, metric);
            }
        }

        /// <summary>
        /// Gets all current node metrics
        /// </summary>
        public IEnumerable<Metric> GetNodeMetrics()
        {
            return _nodeMetrics.Values.ToList();
        }

        /// <summary>
        /// Gets a specific node metric by name
        /// </summary>
        public Metric? GetNodeMetric(string metricName)
        {
            return _nodeMetrics.TryGetValue(metricName, out var metric) ? metric : null;
        }

        /// <summary>
        /// Gets all current device metrics
        /// </summary>
        public IEnumerable<Metric> GetDeviceMetrics(string deviceId)
        {
            if (_deviceMetrics.TryGetValue(deviceId, out var deviceDict))
            {
                return deviceDict.Values.ToList();
            }
            return Enumerable.Empty<Metric>();
        }

        /// <summary>
        /// Gets a specific device metric by name
        /// </summary>
        public Metric? GetDeviceMetric(string deviceId, string metricName)
        {
            if (_deviceMetrics.TryGetValue(deviceId, out var deviceDict))
            {
                return deviceDict.TryGetValue(metricName, out var metric) ? metric : null;
            }
            return null;
        }

        /// <summary>
        /// Gets all known device IDs
        /// </summary>
        public IEnumerable<string> GetDeviceIds()
        {
            return _deviceMetrics.Keys.ToList();
        }

        /// <summary>
        /// Checks if a device exists in the store
        /// </summary>
        public bool HasDevice(string deviceId)
        {
            return _deviceMetrics.ContainsKey(deviceId);
        }

        /// <summary>
        /// Removes a device and all its metrics
        /// </summary>
        public bool RemoveDevice(string deviceId)
        {
            return _deviceMetrics.TryRemove(deviceId, out _);
        }

        /// <summary>
        /// Clears all metrics
        /// </summary>
        public void Clear()
        {
            _nodeMetrics.Clear();
            _deviceMetrics.Clear();
        }

        /// <summary>
        /// Gets metrics that have changed since the last snapshot
        /// </summary>
        public class MetricSnapshot
        {
            public Dictionary<string, object?> NodeMetricValues { get; set; } = new();
            public Dictionary<string, Dictionary<string, object?>> DeviceMetricValues { get; set; } = new();
        }

        private MetricSnapshot? _lastSnapshot;

        /// <summary>
        /// Gets only the metrics that have changed since last snapshot
        /// </summary>
        public (IEnumerable<Metric> nodeMetrics, Dictionary<string, IEnumerable<Metric>> deviceMetrics) GetChangedMetrics()
        {
            lock (_lock)
            {
                var currentSnapshot = TakeSnapshot();
                var changedNodeMetrics = new List<Metric>();
                var changedDeviceMetrics = new Dictionary<string, IEnumerable<Metric>>();

                if (_lastSnapshot == null)
                {
                    // First time - all metrics are "changed"
                    changedNodeMetrics = _nodeMetrics.Values.ToList();
                    foreach (var kvp in _deviceMetrics)
                    {
                        changedDeviceMetrics[kvp.Key] = kvp.Value.Values.ToList();
                    }
                }
                else
                {
                    // Compare node metrics
                    foreach (var metric in _nodeMetrics.Values)
                    {
                        if (!_lastSnapshot.NodeMetricValues.TryGetValue(metric.Name, out var oldValue) ||
                            !Equals(oldValue, metric.Value))
                        {
                            changedNodeMetrics.Add(metric);
                        }
                    }

                    // Compare device metrics
                    foreach (var deviceKvp in _deviceMetrics)
                    {
                        var deviceId = deviceKvp.Key;
                        var deviceChangedMetrics = new List<Metric>();

                        if (!_lastSnapshot.DeviceMetricValues.TryGetValue(deviceId, out var oldDeviceMetrics))
                        {
                            // New device - all metrics are changed
                            deviceChangedMetrics.AddRange(deviceKvp.Value.Values);
                        }
                        else
                        {
                            // Check each metric
                            foreach (var metric in deviceKvp.Value.Values)
                            {
                                if (!oldDeviceMetrics.TryGetValue(metric.Name, out var oldValue) ||
                                    !Equals(oldValue, metric.Value))
                                {
                                    deviceChangedMetrics.Add(metric);
                                }
                            }
                        }

                        if (deviceChangedMetrics.Any())
                        {
                            changedDeviceMetrics[deviceId] = deviceChangedMetrics;
                        }
                    }
                }

                _lastSnapshot = currentSnapshot;
                return (changedNodeMetrics, changedDeviceMetrics);
            }
        }

        /// <summary>
        /// Takes a snapshot of current metric values
        /// </summary>
        private MetricSnapshot TakeSnapshot()
        {
            var snapshot = new MetricSnapshot();

            foreach (var metric in _nodeMetrics.Values)
            {
                snapshot.NodeMetricValues[metric.Name] = metric.Value;
            }

            foreach (var deviceKvp in _deviceMetrics)
            {
                var deviceSnapshot = new Dictionary<string, object?>();
                foreach (var metric in deviceKvp.Value.Values)
                {
                    deviceSnapshot[metric.Name] = metric.Value;
                }
                snapshot.DeviceMetricValues[deviceKvp.Key] = deviceSnapshot;
            }

            return snapshot;
        }

        /// <summary>
        /// Resets the change tracking (forces all metrics to be considered "changed" on next call)
        /// </summary>
        public void ResetChangeTracking()
        {
            lock (_lock)
            {
                _lastSnapshot = null;
            }
        }
    }
}