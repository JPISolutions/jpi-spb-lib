namespace SparkplugB.Publisher
{
    /// <summary>
    /// Represents a Sparkplug-B metric
    /// </summary>
    public class Metric
    {
        /// <summary>
        /// The name of the metric
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The current value of the metric
        /// </summary>
        public object? Value { get; set; }

        /// <summary>
        /// The data type of the metric
        /// </summary>
        public MetricDataType DataType { get; set; }

        /// <summary>
        /// Timestamp in milliseconds since epoch
        /// </summary>
        public ulong Timestamp { get; set; }

        /// <summary>
        /// Optional properties for the metric
        /// </summary>
        public Dictionary<string, string>? Properties { get; set; }

        /// <summary>
        /// Marks a sample that is not the metric's current value: it was acquired earlier and is being
        /// sent late (backfill, store and forward). Sent as is_historical, so a host records it in history
        /// without updating the current value. It never replaces the publisher's stored current value.
        /// </summary>
        public bool IsHistorical { get; set; }

        /// <summary>
        /// Creates a new metric with the current timestamp
        /// </summary>
        public Metric(string name, object? value, MetricDataType dataType)
        {
            Name = name;
            Value = value;
            DataType = dataType;
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Updates the metric value and timestamp
        /// </summary>
        public void UpdateValue(object? value)
        {
            Value = value;
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// A copy, so a stored metric cannot be changed by the caller mutating the object it passed in.
        /// </summary>
        internal Metric Clone() => new(Name, Value, DataType)
        {
            Timestamp = Timestamp,
            Properties = Properties is null ? null : new Dictionary<string, string>(Properties),
            IsHistorical = IsHistorical
        };
    }

    /// <summary>
    /// Sparkplug-B metric data types
    /// </summary>
    public enum MetricDataType
    {
        Unknown = 0,
        Int8 = 1,
        Int16 = 2,
        Int32 = 3,
        Int64 = 4,
        UInt8 = 5,
        UInt16 = 6,
        UInt32 = 7,
        UInt64 = 8,
        Float = 9,
        Double = 10,
        Boolean = 11,
        String = 12,
        DateTime = 13,
        Text = 14,
        UUID = 15,
        DataSet = 16,
        Bytes = 17,
        File = 18,
        Template = 19
    }
}