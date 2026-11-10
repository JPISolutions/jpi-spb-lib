namespace SparkplugB.Publisher.Tests;

public class BackfillBufferTests
{
    private static Metric Sample(int n) => new("Load", (double)n, MetricDataType.Double) { Timestamp = (ulong)n };

    private static IEnumerable<double> Values(IEnumerable<BufferedSample> samples) => samples.Select(s => (double)s.Value!);

    [Fact]
    public void WhenFull_TheOldestSampleIsDropped_AndCounted()
    {
        var buffer = new BackfillBuffer(capacity: 3);

        Assert.False(buffer.Add("A", Sample(1)));
        Assert.False(buffer.Add("A", Sample(2)));
        Assert.False(buffer.Add("A", Sample(3)));
        Assert.True(buffer.Add("A", Sample(4)));
        Assert.True(buffer.Add("A", Sample(5)));

        Assert.Equal(2, buffer.Dropped);
        Assert.Equal(new[] { 3.0, 4.0, 5.0 }, Values(buffer.TakeAll()));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Restore_PutsUnsentSamplesBackAheadOfNewerOnes_WithinCapacity()
    {
        var buffer = new BackfillBuffer(capacity: 4);
        for (var n = 1; n <= 3; n++) buffer.Add("A", Sample(n));
        var taken = buffer.TakeAll();

        buffer.Add("A", Sample(4));
        buffer.Add("A", Sample(5));
        buffer.Restore(taken.Skip(1)); // sample 1 was sent

        Assert.Equal(new[] { 3.0, 4.0, 5.0 }.Prepend(2.0), Values(buffer.TakeAll()));
    }

    [Fact]
    public void ABufferedSample_IsACopy_AndComesBackFlaggedHistorical()
    {
        var buffer = new BackfillBuffer(capacity: 1);
        var metric = Sample(7);
        metric.Properties = new Dictionary<string, string> { ["unit"] = "%" };
        buffer.Add(null, metric);
        metric.Properties["unit"] = "changed";

        var restored = buffer.TakeAll().Single().ToHistoricalMetric();

        Assert.True(restored.IsHistorical);
        Assert.Equal(7UL, restored.Timestamp);
        Assert.Equal("%", restored.Properties!["unit"]);
    }
}
