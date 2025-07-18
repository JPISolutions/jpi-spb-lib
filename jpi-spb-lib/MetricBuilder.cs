namespace SparkplugB.Publisher
{
    /// <summary>
    /// Helper class for building and managing metric collections
    /// </summary>
    public class MetricBuilder
    {
        private readonly Dictionary<string, Metric> _metrics = new();

        /// <summary>
        /// Adds or updates a metric
        /// </summary>
        public MetricBuilder Add(string name, object? value, MetricDataType dataType)
        {
            if (_metrics.ContainsKey(name))
            {
                _metrics[name].UpdateValue(value);
            }
            else
            {
                _metrics[name] = new Metric(name, value, dataType);
            }
            return this;
        }

        /// <summary>
        /// Adds or updates an Int32 metric
        /// </summary>
        public MetricBuilder AddInt32(string name, int value)
        {
            return Add(name, value, MetricDataType.Int32);
        }

        /// <summary>
        /// Adds or updates an Int64 metric
        /// </summary>
        public MetricBuilder AddInt64(string name, long value)
        {
            return Add(name, value, MetricDataType.Int64);
        }

        /// <summary>
        /// Adds or updates a Float metric
        /// </summary>
        public MetricBuilder AddFloat(string name, float value)
        {
            return Add(name, value, MetricDataType.Float);
        }

        /// <summary>
        /// Adds or updates a Double metric
        /// </summary>
        public MetricBuilder AddDouble(string name, double value)
        {
            return Add(name, value, MetricDataType.Double);
        }

        /// <summary>
        /// Adds or updates a Boolean metric
        /// </summary>
        public MetricBuilder AddBoolean(string name, bool value)
        {
            return Add(name, value, MetricDataType.Boolean);
        }

        /// <summary>
        /// Adds or updates a String metric
        /// </summary>
        public MetricBuilder AddString(string name, string value)
        {
            return Add(name, value, MetricDataType.String);
        }

        /// <summary>
        /// Adds or updates a DateTime metric
        /// </summary>
        public MetricBuilder AddDateTime(string name, DateTime value)
        {
            return Add(name, value, MetricDataType.DateTime);
        }

        /// <summary>
        /// Adds or updates a metric with properties
        /// </summary>
        public MetricBuilder AddWithProperties(string name, object? value, MetricDataType dataType, Dictionary<string, string> properties)
        {
            var metric = new Metric(name, value, dataType)
            {
                Properties = properties
            };

            _metrics[name] = metric;
            return this;
        }

        /// <summary>
        /// Removes a metric
        /// </summary>
        public MetricBuilder Remove(string name)
        {
            _metrics.Remove(name);
            return this;
        }

        /// <summary>
        /// Clears all metrics
        /// </summary>
        public MetricBuilder Clear()
        {
            _metrics.Clear();
            return this;
        }

        /// <summary>
        /// Gets a specific metric
        /// </summary>
        public Metric? Get(string name)
        {
            return _metrics.TryGetValue(name, out var metric) ? metric : null;
        }

        /// <summary>
        /// Checks if a metric exists
        /// </summary>
        public bool Contains(string name)
        {
            return _metrics.ContainsKey(name);
        }

        /// <summary>
        /// Builds the metric collection
        /// </summary>
        public IEnumerable<Metric> Build()
        {
            return _metrics.Values.ToList();
        }

        /// <summary>
        /// Gets only metrics that have different values from another builder
        /// </summary>
        public IEnumerable<Metric> GetChangedMetrics(MetricBuilder other)
        {
            var changed = new List<Metric>();

            foreach (var kvp in _metrics)
            {
                var otherMetric = other.Get(kvp.Key);
                if (otherMetric == null || !Equals(kvp.Value.Value, otherMetric.Value))
                {
                    changed.Add(kvp.Value);
                }
            }

            return changed;
        }

        /// <summary>
        /// Creates a new MetricBuilder with initial metrics
        /// </summary>
        public static MetricBuilder Create()
        {
            return new MetricBuilder();
        }

        /// <summary>
        /// Creates common machine metrics
        /// </summary>
        public static MetricBuilder CreateMachineMetrics(string machineType = "Generic")
        {
            return new MetricBuilder()
                .AddString("MachineType", machineType)
                .AddBoolean("Running", false)
                .AddInt64("RunTime", 0)
                .AddInt32("CycleCount", 0)
                .AddDouble("Temperature", 0.0)
                .AddString("Status", "Idle")
                .AddDateTime("LastStartTime", DateTime.UtcNow)
                .AddInt32("ErrorCode", 0);
        }

        /// <summary>
        /// Creates common sensor metrics
        /// </summary>
        public static MetricBuilder CreateSensorMetrics(string sensorType = "Generic")
        {
            return new MetricBuilder()
                .AddString("SensorType", sensorType)
                .AddBoolean("Connected", true)
                .AddDouble("Value", 0.0)
                .AddDouble("MinValue", 0.0)
                .AddDouble("MaxValue", 100.0)
                .AddString("Units", "")
                .AddInt32("Quality", 100)
                .AddDateTime("LastUpdate", DateTime.UtcNow);
        }

        /// <summary>
        /// Creates common power metrics
        /// </summary>
        public static MetricBuilder CreatePowerMetrics()
        {
            return new MetricBuilder()
                .AddFloat("Voltage", 0.0f)
                .AddFloat("Current", 0.0f)
                .AddFloat("Power", 0.0f)
                .AddFloat("PowerFactor", 1.0f)
                .AddFloat("Frequency", 50.0f)
                .AddDouble("Energy", 0.0)
                .AddBoolean("PowerOn", false);
        }
    }
}