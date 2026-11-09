using SparkplugB.Publisher.Tests.Infrastructure;

namespace SparkplugB.Publisher.Tests;

/// <summary>
/// Edge node session behaviour against a real (in-process) MQTT broker, as a Sparkplug host sees it.
/// Every test finishes with <see cref="SparkplugAssert.SessionsAreWellFormed"/>.
/// </summary>
public class SessionTests
{
    private static Metric M(string name, object? value, MetricDataType type = MetricDataType.Double) => new(name, value, type);

    private static bool IsBirth(HostMessage m, string kind, string? device = null) =>
        m.Kind == kind && m.DeviceId == device;

    /// <summary>Messages after the n-th NBIRTH (1-based), including it.</summary>
    private static List<HostMessage> FromBirth(IReadOnlyList<HostMessage> messages, int n)
    {
        var seen = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Kind == "NBIRTH" && ++seen == n)
            {
                return messages.Skip(i).ToList();
            }
        }
        return new List<HostMessage>();
    }

    // ---- Item 1: births on every connection, before any data ---------------------------------

    [Fact]
    public async Task Connect_PublishesNbirthImmediately_EvenWithNoMetrics_StartingAtBdSeqZero()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());

        await publisher.ConnectAsync();

        var messages = await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");
        Assert.Equal(0UL, messages.First(x => x.Kind == "NBIRTH").BdSeq); // tck-id-topics-nbirth-bdseq-increment
        Assert.True(publisher.IsConnected);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task EverythingPublishedBeforeConnecting_IsInTheFirstBirths()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());

        await publisher.PublishNodeBirthAsync(new[] { M("Host Name", "box", MetricDataType.String) }, default);
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 1.0) });
        await publisher.PublishDeviceBirthAsync("B", new[] { M("Load", 2.0) });
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", 1.5) });
        await publisher.ConnectAsync();

        var messages = await broker.WaitForAsync(m => m.Count(x => x.Kind == "DBIRTH") == 2, "both DBIRTHs");
        Assert.Equal(new[] { "NBIRTH/", "DBIRTH/A", "DBIRTH/B" },
            messages.Select(x => $"{x.Kind}/{x.DeviceId}"));
        Assert.Contains("Host Name", messages[0].MetricNames);
        Assert.Equal(1.5, messages.Single(x => IsBirth(x, "DBIRTH", "A")).Metric("Load").DoubleValue);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task NetworkLoss_WillMatchesBirth_ReconnectAdvancesBdSeq_AndRebirthsBeforeAnyData()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 0.0) });
        await publisher.PublishDeviceBirthAsync("B", new[] { M("Load", 0.0) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "B")), "first births");

        await using (var load = new DataLoad(publisher, "A"))
        {
            await Task.Delay(200);
            broker.Cut();

            await broker.WaitForAsync(m => FromBirth(m, 2).Count(x => x.Kind == "DDATA") >= 5,
                "a second session with data flowing");
            Assert.Equal(0, load.Errors); // publishing during the outage must not throw
        }

        var messages = broker.Messages;
        Assert.Equal(0UL, messages.First(x => x.Kind == "NDEATH").BdSeq); // the will matches NBIRTH #1
        Assert.Equal(1UL, messages.Where(x => x.Kind == "NBIRTH").ElementAt(1).BdSeq);
        var secondSession = FromBirth(messages, 2);
        Assert.Contains(secondSession, x => IsBirth(x, "DBIRTH", "A"));
        Assert.Contains(secondSession, x => IsBirth(x, "DBIRTH", "B"));
        SparkplugAssert.SessionsAreWellFormed(messages);
    }

    // ---- Item 2: one sequence, nothing interleaved with a birth sequence ---------------------

    [Theory]
    [InlineData(false)] // the spec says datatype SHOULD NOT be sent in NCMD
    [InlineData(true)]
    public async Task RebirthRequest_RepublishesAllBirths_SameBdSeq_WithNoDataInBetween(bool includeDatatype)
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 0.0) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "first births");

        await using (var load = new DataLoad(publisher, "A"))
        {
            await Task.Delay(100);
            await broker.SendRebirthRequestAsync(includeDatatype);
            await broker.WaitForAsync(m => FromBirth(m, 2).Count(x => x.Kind == "DDATA") >= 5,
                "rebirth followed by data");
            Assert.Equal(0, load.Errors);
        }

        var births = broker.Messages.Where(x => x.Kind == "NBIRTH").ToList();
        Assert.Equal(births[0].BdSeq, births[1].BdSeq); // tck-id-operational-behavior-data-commands-rebirth-action-3
        SparkplugAssert.SessionsAreWellFormed(broker.Messages); // no DDATA between NBIRTH and DBIRTH
    }

    [Fact]
    public async Task SequenceNumbers_WrapFrom255ToZero()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 0.0) });
        await publisher.ConnectAsync();

        for (var i = 1; i <= 300; i++)
        {
            await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", (double)i) });
        }

        await broker.WaitForAsync(m => m.Count(x => x.Kind == "DDATA") == 300, "300 DDATA");
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    // ---- Item 3: only declared metrics, only for birthed devices -----------------------------

    [Fact]
    public async Task NewDeviceMetric_TriggersFullRebirth_InsteadOfUndeclaredData()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 1.0) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "first births");

        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", 2.0), M("Temp", 40.0) });
        var messages = await broker.WaitForAsync(
            m => m.Any(x => IsBirth(x, "DBIRTH", "A") && x.MetricNames.Contains("Temp")), "a DBIRTH declaring Temp");

        Assert.Equal(2, messages.Count(x => x.Kind == "NBIRTH")); // full NBIRTH + DBIRTH sequence
        Assert.Equal(40.0, messages.Last(x => IsBirth(x, "DBIRTH", "A")).Metric("Temp").DoubleValue);

        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Temp", 41.0) });
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "DDATA" && x.MetricNames.Contains("Temp")), "DDATA for Temp");
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task NewNodeMetric_OrChangedDataType_TriggersFullRebirth()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishNodeBirthAsync(new[] { M("Uptime", 1.0) }, default);
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");

        await publisher.PublishNodeMetricsAsync(new[] { M("Uptime", 2.0), M("Version", "1.0", MetricDataType.String) });
        await broker.WaitForAsync(m => m.Count(x => x.Kind == "NBIRTH") == 2, "NBIRTH declaring Version");

        await publisher.PublishNodeMetricsAsync(new[] { M("Uptime", 3, MetricDataType.Int32) }); // type change
        var messages = await broker.WaitForAsync(m => m.Count(x => x.Kind == "NBIRTH") == 3, "NBIRTH redeclaring Uptime");

        Assert.Equal((uint)MetricDataType.Int32, messages.Last(x => x.Kind == "NBIRTH").Metric("Uptime").Datatype);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task DataForAnUnbirthedDevice_BirthsItFirst()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");

        await publisher.PublishDeviceMetricsAsync("C", new[] { M("Load", 3.0) });

        var messages = await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "C")), "DBIRTH for C");
        Assert.Equal(3.0, messages.Single(x => IsBirth(x, "DBIRTH", "C")).Metric("Load").DoubleValue);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task DeviceDeath_TakesItOffline_AndItIsNotRebirthedAfterReconnect()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 1.0) });
        await publisher.PublishDeviceBirthAsync("B", new[] { M("Load", 1.0) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "B")), "first births");

        await publisher.PublishDeviceDeathAsync("A");
        await publisher.PublishDeviceDeathAsync("A"); // a second death for the same device is a no-op
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "DDEATH"), "DDEATH");
        broker.Cut();

        var messages = await broker.WaitForAsync(m => FromBirth(m, 2).Any(x => IsBirth(x, "DBIRTH", "B")), "second session");
        Assert.Single(messages, x => x.Kind == "DDEATH");
        Assert.DoesNotContain(FromBirth(messages, 2), x => IsBirth(x, "DBIRTH", "A"));
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    // ---- Broker unavailable, clean stop, lifecycle --------------------------------------------

    [Fact]
    public async Task BrokerUnreachableAtStart_NothingThrows_AndTheBirthsCarryTheLatestValues()
    {
        await using var broker = await TestBroker.StartAsync(proxyListening: false);
        using var publisher = new SparkplugPublisher(broker.Config());

        await publisher.ConnectAsync(); // must not throw
        Assert.False(publisher.IsConnected);
        await publisher.PublishNodeBirthAsync(new[] { M("Host Name", "box", MetricDataType.String) }, default);
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 1.0) });
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", 7.0) });
        await publisher.HandleRebirthAsync();

        broker.StartProxy();

        var messages = await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "births once the broker is reachable");
        Assert.Equal(7.0, messages.Single(x => IsBirth(x, "DBIRTH", "A")).Metric("Load").DoubleValue);
        Assert.Equal(0UL, messages.Single(x => x.Kind == "NBIRTH").BdSeq); // failed attempts don't use up a bdSeq
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task CleanStop_PublishesNdeathLast_AndTheBrokerDoesNotAlsoSendTheWill()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Load", 0.0) });
        await publisher.ConnectAsync();
        for (var i = 1; i <= 50; i++)
        {
            await publisher.PublishDeviceMetricsAsync("A", new[] { M("Load", (double)i) });
        }

        await publisher.DisconnectAsync();
        await Task.Delay(500);

        var messages = broker.Messages;
        Assert.Equal(50, messages.Count(x => x.Kind == "DDATA"));
        Assert.Equal("NDEATH", messages[^1].Kind);
        Assert.Single(messages, x => x.Kind == "NDEATH");
        Assert.False(publisher.IsConnected);
        SparkplugAssert.SessionsAreWellFormed(messages);
    }

    [Fact]
    public async Task DisposeWithoutDisconnect_StillPublishesNdeath()
    {
        await using var broker = await TestBroker.StartAsync();
        var publisher = new SparkplugPublisher(broker.Config());
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");

        publisher.Dispose();

        await broker.WaitForAsync(m => m.Any(x => x.Kind == "NDEATH"), "NDEATH");
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task ConnectAsyncTwice_IsHarmless()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.ConnectAsync();
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");

        broker.Cut();

        await broker.WaitForAsync(m => m.Count(x => x.Kind == "NBIRTH") == 2 && m.Any(x => x.Kind == "NDEATH"),
            "the will and a second session");
        SparkplugAssert.SessionsAreWellFormed(broker.Messages); // will bdSeq matches, +1 per connection
    }

    [Fact]
    public async Task BdSeq_ContinuesAcrossRestarts_AndWrapsAfter255()
    {
        await using var broker = await TestBroker.StartAsync();
        File.WriteAllText(broker.BdSeqFile, "254");

        using (var first = new SparkplugPublisher(broker.Config()))
        {
            await first.ConnectAsync();
            await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "first NBIRTH");
            await first.DisconnectAsync();
        }
        using (var second = new SparkplugPublisher(broker.Config()))
        {
            await second.ConnectAsync();
            await broker.WaitForAsync(m => m.Count(x => x.Kind == "NBIRTH") == 2, "second NBIRTH");
            await second.DisconnectAsync();
        }

        var births = broker.Messages.Where(x => x.Kind == "NBIRTH").Select(x => x.BdSeq).ToList();
        Assert.Equal(new ulong?[] { 255, 0 }, births);
        Assert.Equal("0", File.ReadAllText(broker.BdSeqFile).Trim());
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task UnencodableValue_ThrowsToTheCaller_AndDoesNotPoisonLaterBirths()
    {
        await using var broker = await TestBroker.StartAsync();
        using var publisher = new SparkplugPublisher(broker.Config());
        await publisher.PublishDeviceBirthAsync("A", new[] { M("Speed", 1, MetricDataType.UInt8) });
        await publisher.ConnectAsync();
        await broker.WaitForAsync(m => m.Any(x => IsBirth(x, "DBIRTH", "A")), "first births");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishDeviceMetricsAsync("A", new[] { M("Speed", 300, MetricDataType.UInt8) }));
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Speed", 7, MetricDataType.UInt8) });
        await publisher.HandleRebirthAsync();

        var messages = await broker.WaitForAsync(m => m.Count(x => IsBirth(x, "DBIRTH", "A")) == 2, "rebirth");
        Assert.Equal(7u, messages.Last(x => IsBirth(x, "DBIRTH", "A")).Metric("Speed").IntValue);
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    /// <summary>Publishes DDATA for a device in a tight loop until disposed, counting exceptions.</summary>
    private sealed class DataLoad : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _errors;

        public DataLoad(SparkplugPublisher publisher, string deviceId)
        {
            _loop = Task.Run(async () =>
            {
                var i = 0;
                while (!_stop.IsCancellationRequested)
                {
                    try { await publisher.PublishDeviceMetricsAsync(deviceId, new[] { M("Load", (double)i++) }); }
                    catch { Interlocked.Increment(ref _errors); }
                    await Task.Delay(5);
                }
            });
        }

        public int Errors => Volatile.Read(ref _errors);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
        }
    }
}
