namespace SparkplugB.Publisher
{
    /// <summary>
    /// Samples published while there was no live session, kept so they can be sent as history
    /// (is_historical) after the next birth sequence. Bounded: when full, the oldest sample is dropped.
    /// Not thread-safe; the publisher uses it under its publish lock.
    /// </summary>
    internal sealed class BackfillBuffer
    {
        private readonly Queue<BufferedSample> _samples = new();

        public BackfillBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");
            Capacity = capacity;
        }

        public int Capacity { get; }

        public int Count => _samples.Count;

        /// <summary>Samples dropped because the buffer was full, since the last <see cref="ResetDropped"/>.</summary>
        public long Dropped { get; private set; }

        /// <summary>Adds a sample (deviceId null for a node metric).</summary>
        /// <returns>True if the buffer was full and the oldest sample was dropped to make room.</returns>
        public bool Add(string? deviceId, Metric metric)
        {
            var dropped = false;
            if (_samples.Count >= Capacity)
            {
                _samples.Dequeue();
                Dropped++;
                dropped = true;
            }

            _samples.Enqueue(BufferedSample.From(deviceId, metric));
            return dropped;
        }

        /// <summary>Removes and returns every sample, oldest first.</summary>
        public List<BufferedSample> TakeAll()
        {
            var all = _samples.ToList();
            _samples.Clear();
            return all;
        }

        /// <summary>Puts back samples taken by <see cref="TakeAll"/> that could not be sent, keeping their order.</summary>
        public void Restore(IEnumerable<BufferedSample> unsent)
        {
            var newer = _samples.ToList();
            _samples.Clear();
            foreach (var sample in unsent.Concat(newer).TakeLast(Capacity))
            {
                _samples.Enqueue(sample);
            }
        }

        public void ResetDropped() => Dropped = 0;
    }

    /// <summary>One buffered metric value, with the time it was acquired.</summary>
    internal sealed record BufferedSample(
        string? DeviceId, string Name, object? Value, MetricDataType DataType, ulong Timestamp, Dictionary<string, string>? Properties)
    {
        public static BufferedSample From(string? deviceId, Metric metric) => new(
            deviceId, metric.Name, metric.Value, metric.DataType, metric.Timestamp,
            metric.Properties is null ? null : new Dictionary<string, string>(metric.Properties));

        public Metric ToHistoricalMetric() => new(Name, Value, DataType)
        {
            Timestamp = Timestamp,
            Properties = Properties,
            IsHistorical = true
        };
    }
}
