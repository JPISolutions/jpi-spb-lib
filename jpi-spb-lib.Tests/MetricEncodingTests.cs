using System.Globalization;
using Com.Cirruslink.Sparkplug.Protobuf;
using ValueCase = Com.Cirruslink.Sparkplug.Protobuf.Payload.Types.Metric.ValueOneofCase;

namespace SparkplugB.Publisher.Tests;

/// <summary>
/// How metric values are written into the Sparkplug B protobuf payload.
/// Spec references are Sparkplug 3.0.0 "Datatype Details" and the tck-ids named on each test.
/// </summary>
public class MetricEncodingTests
{
    private static Payload.Types.Metric Encode(MetricDataType type, object? value, string name = "m")
    {
        var bytes = SparkplugMessageFactory.CreatePayload(new[] { new Metric(name, value, type) }, sequence: 0);
        return Payload.Parser.ParseFrom(bytes).Metrics.Single();
    }

    // ---- Signed integers -------------------------------------------------------------------
    // Int8/16/32 travel in a uint32 field and Int64 in a uint64 field. Negative values are sent as
    // their two's-complement bits, sign-extended to the field width (the Eclipse Tahu convention), so
    // a host truncating to the declared width gets the original value back.

    [Theory]
    [InlineData(MetricDataType.Int8, -5, 0xFFFFFFFBu)]
    [InlineData(MetricDataType.Int8, 127, 127u)]
    [InlineData(MetricDataType.Int16, -300, 0xFFFFFED4u)]
    [InlineData(MetricDataType.Int32, -5, 0xFFFFFFFBu)]
    [InlineData(MetricDataType.Int32, int.MinValue, 0x80000000u)]
    public void Signed32_NegativeValues_AreTwosComplement(MetricDataType type, int value, uint expected)
    {
        var m = Encode(type, value);

        Assert.Equal(ValueCase.IntValue, m.ValueCase);
        Assert.Equal(expected, m.IntValue);
    }

    [Fact]
    public void Signed_RoundTrip_ThroughHostStyleDecode()
    {
        Assert.Equal(-5, unchecked((sbyte)Encode(MetricDataType.Int8, -5).IntValue));
        Assert.Equal(-300, unchecked((short)Encode(MetricDataType.Int16, -300).IntValue));
        Assert.Equal(-5, unchecked((int)Encode(MetricDataType.Int32, -5).IntValue));
        Assert.Equal(-5L, unchecked((long)Encode(MetricDataType.Int64, -5L).LongValue));
    }

    [Fact]
    public void Int64_Negative_IsTwosComplement()
    {
        Assert.Equal(0xFFFFFFFFFFFFFFFBUL, Encode(MetricDataType.Int64, -5L).LongValue);
    }

    // ---- Any convertible CLR type is accepted; the declared Sparkplug type decides the encoding ----

    public static TheoryData<MetricDataType, object, ulong> AcceptedClrTypes => new()
    {
        { MetricDataType.Int32, 42L, 42 },
        { MetricDataType.Int32, (short)42, 42 },
        { MetricDataType.Int32, 42.0, 42 },
        { MetricDataType.Int32, "42", 42 },
        { MetricDataType.Int64, 42, 42 },
        { MetricDataType.UInt8, 255, 255 },
        { MetricDataType.UInt16, 65535, 65535 },
        { MetricDataType.UInt32, 5, 5 },                       // boxed int
        { MetricDataType.UInt32, uint.MaxValue, uint.MaxValue },
        { MetricDataType.UInt64, 5, 5 },                       // boxed int
        { MetricDataType.UInt64, 5L, 5 },
        { MetricDataType.UInt64, ulong.MaxValue, ulong.MaxValue },
    };

    [Theory, MemberData(nameof(AcceptedClrTypes))]
    public void Integers_AcceptAnyConvertibleClrType(MetricDataType type, object value, ulong expected)
    {
        var m = Encode(type, value);

        var actual = m.ValueCase == ValueCase.LongValue ? m.LongValue : m.IntValue;
        Assert.Equal(expected, actual);
    }

    // ---- A value that doesn't fit the declared type is an error naming the metric ----

    [Theory]
    [InlineData(MetricDataType.Int8, 200)]
    [InlineData(MetricDataType.Int16, 40000)]
    [InlineData(MetricDataType.UInt8, 256)]
    [InlineData(MetricDataType.UInt8, -1)]
    [InlineData(MetricDataType.UInt16, 70000)]
    [InlineData(MetricDataType.UInt32, -1)]
    [InlineData(MetricDataType.UInt64, -1)]
    public void Integers_OutOfRangeForDeclaredType_Throw(MetricDataType type, int value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Encode(type, value, name: "Pump/Speed"));

        Assert.Contains("Pump/Speed", ex.Message);
    }

    // ---- Null ------------------------------------------------------------------------------
    // tck-id-operational-behavior-data-publish-nbirth-values / -dbirth-values: a null value MUST be
    // sent with is_null = true and MUST NOT have a value.

    public static TheoryData<MetricDataType> SupportedTypes => new()
    {
        MetricDataType.Int8, MetricDataType.Int16, MetricDataType.Int32, MetricDataType.Int64,
        MetricDataType.UInt8, MetricDataType.UInt16, MetricDataType.UInt32, MetricDataType.UInt64,
        MetricDataType.Float, MetricDataType.Double, MetricDataType.Boolean, MetricDataType.String,
        MetricDataType.DateTime, MetricDataType.Text, MetricDataType.UUID, MetricDataType.Bytes,
        MetricDataType.File,
    };

    [Theory, MemberData(nameof(SupportedTypes))]
    public void Null_IsSentAsIsNull_WithNoValue(MetricDataType type)
    {
        var m = Encode(type, null);

        Assert.True(m.IsNull);
        Assert.Equal(ValueCase.None, m.ValueCase);
        Assert.Equal((uint)type, m.Datatype);
    }

    // ---- Boolean ---------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2.5, true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Boolean_AcceptsBoolNumbersAndStrings(object value, bool expected)
    {
        var m = Encode(MetricDataType.Boolean, value);

        Assert.Equal(ValueCase.BooleanValue, m.ValueCase);
        Assert.Equal(expected, m.BooleanValue);
    }

    [Fact]
    public void Boolean_UnparseableString_Throws()
    {
        Assert.Throws<ArgumentException>(() => Encode(MetricDataType.Boolean, "maybe"));
    }

    // ---- Floating point, strings, culture ------------------------------------------------------

    [Fact]
    public void Conversions_UseInvariantCulture_NotTheMachineRegion()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // ',' decimal, '.' grouping
        try
        {
            Assert.Equal(1.5, Encode(MetricDataType.Double, "1.5").DoubleValue);
            Assert.Equal(1.5f, Encode(MetricDataType.Float, "1.5").FloatValue);
            Assert.Equal("1.5", Encode(MetricDataType.String, 1.5).StringValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Uuid_IsItsStringForm()
    {
        var id = Guid.Parse("8f14e45f-ceea-467f-a0e6-2f1a3a7f8b2c");

        Assert.Equal("8f14e45f-ceea-467f-a0e6-2f1a3a7f8b2c", Encode(MetricDataType.UUID, id).StringValue);
    }

    // ---- DateTime: uint64 milliseconds since the Unix epoch ------------------------------------

    [Fact]
    public void DateTime_IsMillisecondsSinceEpoch()
    {
        var instant = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var ms = (ulong)instant.ToUnixTimeMilliseconds();

        Assert.Equal(ms, Encode(MetricDataType.DateTime, instant).LongValue);
        Assert.Equal(ms, Encode(MetricDataType.DateTime, instant.UtcDateTime).LongValue);
        Assert.Equal(ms, Encode(MetricDataType.DateTime, ms).LongValue);
    }

    [Fact]
    public void DateTime_BeforeEpoch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Encode(MetricDataType.DateTime, new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    // ---- Bytes / File ----------------------------------------------------------------------

    [Fact]
    public void Bytes_AreCopied()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, Encode(MetricDataType.Bytes, new byte[] { 1, 2, 3 }).BytesValue.ToByteArray());
    }

    [Fact]
    public void Bytes_NonByteArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => Encode(MetricDataType.Bytes, "abc"));
    }

    // ---- Types this library does not encode are rejected, not silently sent as null ----------

    [Theory]
    [InlineData(MetricDataType.Unknown)]
    [InlineData(MetricDataType.DataSet)]
    [InlineData(MetricDataType.Template)]
    public void UnsupportedTypes_Throw(MetricDataType type)
    {
        Assert.Throws<NotSupportedException>(() => Encode(type, 1));
    }

    // ---- is_historical -------------------------------------------------------------------------

    [Fact]
    public void IsHistorical_IsEncoded_OnlyWhenSet()
    {
        var historical = new Metric("m", 1.0, MetricDataType.Double) { IsHistorical = true };
        var bytes = SparkplugMessageFactory.CreatePayload(new[] { historical, new Metric("n", 2.0, MetricDataType.Double) }, 0);
        var metrics = Payload.Parser.ParseFrom(bytes).Metrics;

        Assert.True(metrics[0].IsHistorical);
        Assert.False(metrics[1].HasIsHistorical);
    }

    // ---- Birth and death certificates ------------------------------------------------------

    [Fact]
    public void NodeBirth_CarriesBdSeq_RebirthControl_AndSeqZero()
    {
        var bytes = SparkplugMessageFactory.CreateNodeBirth(
            new[] { new Metric("Temp", -5, MetricDataType.Int32) }, bdSeq: 7, sequence: 0);
        var p = Payload.Parser.ParseFrom(bytes);

        Assert.Equal(0UL, p.Seq);
        Assert.Equal(7UL, p.Metrics.Single(m => m.Name == "bdSeq").LongValue);

        // tck-id-topics-nbirth-rebirth-metric: Boolean, value false (present, not omitted)
        var rebirth = p.Metrics.Single(m => m.Name == "Node Control/Rebirth");
        Assert.Equal((uint)MetricDataType.Boolean, rebirth.Datatype);
        Assert.Equal(ValueCase.BooleanValue, rebirth.ValueCase);
        Assert.False(rebirth.BooleanValue);

        Assert.Equal(0xFFFFFFFBu, p.Metrics.Single(m => m.Name == "Temp").IntValue);
    }

    [Fact]
    public void NodeDeath_HasOnlyBdSeq_AndNoSeq()
    {
        // tck-id-topics-ndeath-payload (single bdSeq metric) and tck-id-topics-ndeath-seq (no seq)
        var p = Payload.Parser.ParseFrom(SparkplugMessageFactory.CreateNodeDeath(7));

        Assert.False(p.HasSeq);
        var only = Assert.Single(p.Metrics);
        Assert.Equal("bdSeq", only.Name);
        Assert.Equal(7UL, only.LongValue);
    }
}
