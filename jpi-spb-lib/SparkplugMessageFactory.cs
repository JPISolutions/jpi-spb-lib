using Com.Cirruslink.Sparkplug.Protobuf;
using Google.Protobuf;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Factory for creating Sparkplug-B protobuf messages
    /// </summary>
    internal class SparkplugMessageFactory
    {
        /// <summary>
        /// Creates a Sparkplug-B payload from metrics
        /// </summary>
        public static byte[] CreatePayload(IEnumerable<Metric> metrics, ulong sequence, ulong? timestamp = null)
        {
            var payload = new Payload
            {
                Timestamp = timestamp ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Seq = sequence
            };

            foreach (var metric in metrics)
            {
                payload.Metrics.Add(ConvertToProtobufMetric(metric));
            }

            return payload.ToByteArray();
        }

        /// <summary>
        /// Creates a Node Birth (NBIRTH) payload
        /// </summary>
        public static byte[] CreateNodeBirth(IEnumerable<Metric> metrics, ulong bdSeq, ulong sequence = 0)
        {
            var allMetrics = new List<Metric>
            {
                new Metric("bdSeq", bdSeq, MetricDataType.UInt64),
                new Metric("Node Control/Rebirth", false, MetricDataType.Boolean)
            };
            allMetrics.AddRange(metrics);

            return CreatePayload(allMetrics, sequence);
        }

        /// <summary>
        /// Creates a Node Death (NDEATH) payload
        /// </summary>
        public static byte[] CreateNodeDeath(ulong bdSeq)
        {
            var payload = new Payload();

            var bdSeqMetric = new Payload.Types.Metric
            {
                Name = "bdSeq",
                Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Datatype = (uint)MetricDataType.UInt64,
                LongValue = bdSeq
            };

            payload.Metrics.Add(bdSeqMetric);

            return payload.ToByteArray();
        }

        /// <summary>
        /// Creates a Device Death (DDEATH) payload
        /// </summary>
        public static byte[] CreateDeviceDeath(ulong sequence, ulong? timestamp = null)
        {
            var payload = new Payload
            {
                Timestamp = timestamp ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Seq = sequence
            };

            return payload.ToByteArray();
        }

        /// <summary>
        /// Converts our Metric to protobuf Metric
        /// </summary>
        private static Payload.Types.Metric ConvertToProtobufMetric(Metric metric)
        {
            var protoMetric = new Payload.Types.Metric
            {
                Name = metric.Name,
                Timestamp = metric.Timestamp,
                Datatype = (uint)metric.DataType
            };

            // Set the value based on data type
            switch (metric.DataType)
            {
                case MetricDataType.Int8:
                case MetricDataType.Int16:
                case MetricDataType.Int32:
                case MetricDataType.UInt8:
                case MetricDataType.UInt16:
                    if (metric.Value != null)
                        protoMetric.IntValue = Convert.ToUInt32(metric.Value);
                    break;

                case MetricDataType.UInt32:
                    if (metric.Value != null)
                        protoMetric.IntValue = (uint)metric.Value;
                    break;

                case MetricDataType.Int64:
                    if (metric.Value != null)
                        protoMetric.LongValue = Convert.ToUInt64(metric.Value);
                    break;

                case MetricDataType.UInt64:
                    if (metric.Value != null)
                        protoMetric.LongValue = (ulong)metric.Value;
                    break;

                case MetricDataType.Float:
                    if (metric.Value != null)
                        protoMetric.FloatValue = Convert.ToSingle(metric.Value);
                    break;

                case MetricDataType.Double:
                    if (metric.Value != null)
                        protoMetric.DoubleValue = Convert.ToDouble(metric.Value);
                    break;

                case MetricDataType.Boolean:
                    if (metric.Value != null)
                        protoMetric.BooleanValue = Convert.ToBoolean(metric.Value);
                    break;

                case MetricDataType.String:
                case MetricDataType.Text:
                case MetricDataType.UUID:
                    protoMetric.StringValue = metric.Value?.ToString() ?? string.Empty;
                    break;

                case MetricDataType.DateTime:
                    if (metric.Value is DateTime dt)
                        protoMetric.LongValue = (ulong)new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                    else if (metric.Value is DateTimeOffset dto)
                        protoMetric.LongValue = (ulong)dto.ToUnixTimeMilliseconds();
                    else if (metric.Value != null)
                        protoMetric.LongValue = Convert.ToUInt64(metric.Value);
                    break;

                case MetricDataType.Bytes:
                case MetricDataType.File:
                    if (metric.Value is byte[] bytes)
                        protoMetric.BytesValue = ByteString.CopyFrom(bytes);
                    break;

                default:
                    protoMetric.IsNull = true;
                    break;
            }

            // Add properties if any
            if (metric.Properties != null && metric.Properties.Count > 0)
            {
                var propertySet = new Payload.Types.PropertySet();

                foreach (var prop in metric.Properties)
                {
                    propertySet.Keys.Add(prop.Key);

                    var propValue = new Payload.Types.PropertyValue
                    {
                        Type = 12, // String type
                        StringValue = prop.Value
                    };

                    propertySet.Values.Add(propValue);
                }

                protoMetric.Properties = propertySet;
            }

            return protoMetric;
        }
    }
}