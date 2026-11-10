using System.Diagnostics;
using SparkplugB.Publisher.Tests.Infrastructure;
using ProtoMetric = Com.Cirruslink.Sparkplug.Protobuf.Payload.Types.Metric;

namespace SparkplugB.Publisher.Tests;

/// <summary>
/// History backfill: samples published during an outage are sent as is_historical after the next
/// birth sequence. Every test finishes with <see cref="SparkplugAssert.SessionsAreWellFormed"/>.
/// </summary>
[Collection("Broker")]
public class BackfillTests
{
    private static readonly ulong T0 = (ulong)DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

    private static Metric M(string name, double value) => new(name, value, MetricDataType.Double);

    /// <summary>A sample acquired at T0 + n seconds.</summary>
    private static Metric Sample(string name, int n) => new(name, (double)n, MetricDataType.Double) { Timestamp = At(n) };

    private static ulong At(int n) => T0 + (ulong)n * 1000;

    private static SparkplugConfiguration WithBackfill(TestBroker broker, int maxSamples = 10_000)
    {
        var config = broker.Config();
        config.BackfillEnabled = true;
        config.BackfillMaxSamples = maxSamples;
        return config;
    }

    private static bool IsBirth(HostMessage m, string kind, string? device = null) => m.Kind == kind && m.DeviceId == device;

    private static List<HostMessage> FromBirth(IReadOnlyList<HostMessage> messages, int n)
    {
        var seen = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Kind == "NBIRTH" && ++seen == n) return messages.Skip(i).ToList();
        }
        return new List<HostMessage>();
    }

    private static List<ProtoMetric> History(IEnumerable<HostMessage> messages, string kind = "DDATA") =>
        messages.Where(x => x.Kind == kind).SelectMany(x => x.HistoricalMetrics).ToList();

    /// <summary>Makes the broker unreachable and waits until the publisher has noticed.</summary>
    private static async Task GoOfflineAsync(TestBroker broker, SparkplugPublisher publisher)
    {
        broker.StopProxy();
        var stopwatch = Stopwatch.StartNew();
        while (publisher.IsConnected && stopwatch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(10);
        }
        Assert.False(publisher.IsConnected);
    }

    private static async Task StartAsync(TestBroker broker, SparkplugPublisher publisher)
    {
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 0) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "first births");
    }

    [Fact]
    public async Task OffByDefault_TheBirthsCarryTheLatestValue_AndNothingIsBackfilled()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await StartAsync(broker, publisher);

        await GoOfflineAsync(broker, publisher);
        for (var n = 1; n <= 10; n++) await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
        broker.StartProxy();

        var messages = await broker.WaitForAsync(m => FromBirth(m, 2).Any(x => IsBirth(x, "DBIRTH", "A")), "second session");
        await Task.Delay(300);

        Assert.Equal(10.0, FromBirth(messages, 2).Single(x => IsBirth(x, "DBIRTH", "A")).Metric("Load").DoubleValue);
        Assert.Empty(History(broker.Messages));
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task OutageSamples_AreSentAsHistory_AfterTheBirths_OldestFirst_WithOriginalTimestamps()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(WithBackfill(broker));
        await publisher.PublishNodeBirthAsync(new[] { M("Uptime", 0) }, default);
        await StartAsync(broker, publisher);

        await GoOfflineAsync(broker, publisher);
        for (var n = 1; n <= 20; n++) await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
        for (var n = 1; n <= 5; n++) await publisher.PublishNodeMetricsAsync(new[] { Sample("Uptime", n) });
        broker.StartProxy();

        var messages = await broker.WaitForAsync(
            m => History(FromBirth(m, 2)).Count == 19 && History(FromBirth(m, 2), "NDATA").Count == 4, "the backfill");
        var session = FromBirth(messages, 2);

        // The births carry the newest sample as current state...
        Assert.Equal(5.0, session[0].Metric("Uptime").DoubleValue);
        Assert.Equal(20.0, session.Single(x => IsBirth(x, "DBIRTH", "A")).Metric("Load").DoubleValue);

        // ...and every earlier sample follows as history, oldest first, at the time it was acquired.
        var load = History(session);
        Assert.Equal(Enumerable.Range(1, 19).Select(n => (double)n), load.Select(x => x.DoubleValue));
        Assert.Equal(Enumerable.Range(1, 19).Select(At), load.Select(x => x.Timestamp));
        Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, History(session, "NDATA").Select(x => x.DoubleValue));

        // Live data resumes afterwards, as current state.
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", 21) });
        messages = await broker.WaitForAsync(
            m => m.Any(x => x.Kind == "DDATA" && x.Payload.Metrics.Any(v => !v.IsHistorical && v.DoubleValue == 21.0)), "live data");
        var live = messages.Last(x => x.Kind == "DDATA");
        Assert.True(messages.Where(x => x.Index < live.Index).Count(x => x.HistoricalMetrics.Count > 0) > 0);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task FullBuffer_DropsTheOldest_AndWarnsOncePerOutage()
    {
        await using var broker = await TestBroker.StartAsync();
        var log = new CollectingSink();
        using var publisher = new SparkplugPublisher(WithBackfill(broker, maxSamples: 5));
        publisher.SetLogger(log.CreateLogger());
        await StartAsync(broker, publisher);

        await GoOfflineAsync(broker, publisher);
        for (var n = 1; n <= 20; n++) await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
        Assert.Equal(1, log.Count("Backfill buffer is full"));

        broker.StartProxy();
        var messages = await broker.WaitForAsync(m => History(FromBirth(m, 2)).Count == 4, "the backfill");
        // The buffer held 16-20; 20 is the current value the DBIRTH carries.
        Assert.Equal(new[] { 16.0, 17.0, 18.0, 19.0 }, History(FromBirth(messages, 2)).Select(x => x.DoubleValue));

        // The reconnect emptied the buffer, so the next outage warns again.
        await GoOfflineAsync(broker, publisher);
        for (var n = 21; n <= 40; n++) await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
        broker.StartProxy();
        await broker.WaitForAsync(m => History(FromBirth(m, 3)).Count == 4, "the second backfill");

        Assert.Equal(2, log.Count("Backfill buffer is full"));
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task LargeBackfill_IsSplitIntoMessagesOfAtMost500Metrics()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(WithBackfill(broker));
        await StartAsync(broker, publisher);

        await GoOfflineAsync(broker, publisher);
        for (var n = 1; n <= 1201; n++) await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
        broker.StartProxy();

        var messages = await broker.WaitForAsync(m => History(FromBirth(m, 2)).Count == 1200, "the backfill");
        var sizes = FromBirth(messages, 2).Where(x => x.Kind == "DDATA").Select(x => x.HistoricalMetrics.Count);
        Assert.Equal(new[] { 500, 500, 200 }, sizes);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task DeviceRetiredDuringTheOutage_ItsSamplesAreNotSent()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(WithBackfill(broker));
        await publisher.PublishDeviceBirthAsync("B", new[] { M("Load", 0) });
        await StartAsync(broker, publisher);

        await GoOfflineAsync(broker, publisher);
        for (var n = 1; n <= 3; n++)
        {
            await publisher.PublishDeviceMetricsAsync("A", new[] { Sample("Load", n) });
            await publisher.PublishDeviceMetricsAsync("B", new[] { Sample("Load", n) });
        }
        await publisher.PublishDeviceDeathAsync("B");
        broker.StartProxy();

        var messages = await broker.WaitForAsync(m => History(FromBirth(m, 2)).Count == 2, "the backfill for A");
        await Task.Delay(300);

        Assert.DoesNotContain(FromBirth(broker.Messages, 2), x => x.DeviceId == "B");
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task CallerSuppliedHistoricalSample_IsSentAsHistory_AndLeavesTheCurrentValueAlone()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config()); // independent of backfill
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 10) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "first births");

        var old = Sample("Load", 3);
        old.IsHistorical = true;
        await publisher.PublishDeviceMetricsAsync("A", new[] { old });
        await broker.WaitForAsync(m => History(m).Any(x => x.DoubleValue == 3.0 && x.Timestamp == At(3)), "the historical sample");
        Assert.Equal(10.0, publisher.GetDeviceMetric("A", "Load")!.Value);

        // A historical sample of a new metric declares it in a rebirth, then goes out as history.
        var temp = Sample("Temp", 30);
        temp.IsHistorical = true;
        await publisher.PublishDeviceMetricsAsync("A", new[] { temp });
        await broker.WaitForAsync(m => History(m).Any(x => x.Name == "Temp"), "Temp as history");
        Assert.Contains(broker.Messages, x => IsBirth(x, "DBIRTH", "A") && x.MetricNames.Contains("Temp"));
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }
}
