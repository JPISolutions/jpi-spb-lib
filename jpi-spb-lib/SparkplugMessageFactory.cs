using System.Globalization;
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
        /// Throws if any metric cannot be encoded, so a bad value is rejected before it is stored and
        /// later carried by a birth certificate.
        /// </summary>
        public static void Validate(IEnumerable<Metric> metrics)
        {
            foreach (var metric in metrics)
            {
                ConvertToProtobufMetric(metric);
            }
        }

        /// <summary>
        /// Converts our Metric to protobuf Metric.
        /// </summary>
        /// <exception cref="NotSupportedException">The metric's data type is not one this library encodes.</exception>
        /// <exception cref="ArgumentException">The value cannot be represented as the metric's data type.</exception>
        private static Payload.Types.Metric ConvertToProtobufMetric(Metric metric)
        {
            if (!IsSupported(metric.DataType))
            {
                throw new NotSupportedException(
                    $"Metric '{metric.Name}': data type {metric.DataType} is not supported by this library.");
            }

            var protoMetric = new Payload.Types.Metric
            {
                Name = metric.Name,
                Timestamp = metric.Timestamp,
                Datatype = (uint)metric.DataType
            };

            if (metric.Value == null)
            {
                // A null value is sent as is_null with no value set
                // (tck-id-operational-behavior-data-publish-nbirth-values / -dbirth-values).
                protoMetric.IsNull = true;
            }
            else
            {
                try
                {
                    SetValue(protoMetric, metric.DataType, metric.Value);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    throw new ArgumentException(
                        $"Metric '{metric.Name}': value '{metric.Value}' ({metric.Value.GetType().Name}) " +
                        $"cannot be encoded as {metric.DataType}: {ex.Message}", ex);
                }
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

        private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

        private static bool IsSupported(MetricDataType type) => type switch
        {
            MetricDataType.Int8 or MetricDataType.Int16 or MetricDataType.Int32 or MetricDataType.Int64 or
            MetricDataType.UInt8 or MetricDataType.UInt16 or MetricDataType.UInt32 or MetricDataType.UInt64 or
            MetricDataType.Float or MetricDataType.Double or MetricDataType.Boolean or
            MetricDataType.String or MetricDataType.Text or MetricDataType.UUID or
            MetricDataType.DateTime or MetricDataType.Bytes or MetricDataType.File => true,
            _ => false
        };

        /// <summary>
        /// Writes a non-null value into the protobuf field the Sparkplug spec assigns to its data type.
        /// Any convertible CLR type is accepted; the declared data type decides the encoding, and a value
        /// that does not fit it throws (FormatException, InvalidCastException or OverflowException).
        /// Conversions use the invariant culture so a machine's regional settings cannot change a value.
        /// </summary>
        private static void SetValue(Payload.Types.Metric m, MetricDataType type, object value)
        {
            switch (type)
            {
                // Signed types travel in unsigned protobuf fields (uint32 / uint64) as their two's-complement
                // bits, sign-extended to the field width, as Eclipse Tahu does. Converting through the signed
                // type of the declared width first also rejects values that do not fit that width.
                case MetricDataType.Int8:
                    m.IntValue = unchecked((uint)Convert.ToSByte(value, Invariant));
                    break;
                case MetricDataType.Int16:
                    m.IntValue = unchecked((uint)Convert.ToInt16(value, Invariant));
                    break;
                case MetricDataType.Int32:
                    m.IntValue = unchecked((uint)Convert.ToInt32(value, Invariant));
                    break;
                case MetricDataType.Int64:
                    m.LongValue = unchecked((ulong)Convert.ToInt64(value, Invariant));
                    break;

                case MetricDataType.UInt8:
                    m.IntValue = Convert.ToByte(value, Invariant);
                    break;
                case MetricDataType.UInt16:
                    m.IntValue = Convert.ToUInt16(value, Invariant);
                    break;
                case MetricDataType.UInt32:
                    m.IntValue = Convert.ToUInt32(value, Invariant);
                    break;
                case MetricDataType.UInt64:
                    m.LongValue = Convert.ToUInt64(value, Invariant);
                    break;

                case MetricDataType.Float:
                    m.FloatValue = Convert.ToSingle(value, Invariant);
                    break;
                case MetricDataType.Double:
                    m.DoubleValue = Convert.ToDouble(value, Invariant);
                    break;
                case MetricDataType.Boolean:
                    m.BooleanValue = ToBoolean(value);
                    break;

                case MetricDataType.String:
                case MetricDataType.Text:
                case MetricDataType.UUID:
                    m.StringValue = Convert.ToString(value, Invariant) ?? string.Empty;
                    break;

                case MetricDataType.DateTime:
                    m.LongValue = ToEpochMilliseconds(value);
                    break;

                case MetricDataType.Bytes:
                case MetricDataType.File:
                    m.BytesValue = value is byte[] bytes
                        ? ByteString.CopyFrom(bytes)
                        : throw new InvalidCastException($"expected byte[], got {value.GetType().Name}");
                    break;
            }
        }

        /// <summary>
        /// A bool, a bool string ("true"/"False"), or a number (any non-zero value is true, which covers
        /// SCADA digitals that arrive as 0/1, including as numeric strings).
        /// </summary>
        private static bool ToBoolean(object value)
        {
            switch (value)
            {
                case bool b:
                    return b;
                case string s:
                    if (bool.TryParse(s, out var parsed)) return parsed;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number != 0d;
                    throw new FormatException($"'{s}' is not a Boolean or a number");
                default:
                    return Convert.ToDouble(value, Invariant) != 0d;
            }
        }

        /// <summary>
        /// Sparkplug DateTime is unsigned milliseconds since the Unix epoch, so an instant before 1970
        /// cannot be represented and throws rather than wrapping to a huge value.
        /// </summary>
        private static ulong ToEpochMilliseconds(object value) => value switch
        {
            DateTimeOffset dto => checked((ulong)dto.ToUnixTimeMilliseconds()),
            DateTime dt => checked((ulong)new DateTimeOffset(dt).ToUnixTimeMilliseconds()),
            _ => Convert.ToUInt64(value, Invariant)
        };
    }
}