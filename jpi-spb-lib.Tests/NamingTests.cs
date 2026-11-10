using SparkplugB.Publisher.Tests.Infrastructure;

namespace SparkplugB.Publisher.Tests;

/// <summary>
/// Names that break Ignition's rules: renamed by default, left out when RenameInvalidNames is off.
/// Also the bdSeq state-file problems, which must be logged rather than stop the node.
/// </summary>
[Collection("Broker")]
public class NamingTests
{
    private static Metric M(string name, double value) => new(name, value, MetricDataType.Double);

    private static bool IsBirth(HostMessage m, string kind, string? device = null) => m.Kind == kind && m.DeviceId == device;

    private static (SparkplugPublisher Publisher, CollectingSink Log) Create(SparkplugConfiguration config)
    {
        var log = new CollectingSink();
        var publisher = new SparkplugPublisher(config);
        publisher.SetLogger(log.CreateLogger());
        return (publisher, log);
    }

    [Fact]
    public async Task RenameOn_InvalidNamesArePublishedUnderTheirValidForm_WithOneWarningEach()
    {
        await using var broker = await TestBroker.StartAsync();
        var (publisher, log) = Create(broker.Config());
        using var _ = publisher;

        await publisher.PublishDeviceBirthAsync("Disk.C", new[] { M("Usage %", 1) });
        await publisher.ConnectAsync();
        for (var i = 2; i <= 4; i++) await publisher.PublishDeviceMetricsAsync("Disk.C", new[] { M("Usage %", i) });

        var messages = await broker.WaitForAsync(m => m.Count(x => x.Kind == "DDATA") == 3, "three DDATA");
        Assert.Contains("Usage _", messages.Single(x => IsBirth(x, "DBIRTH", "Disk_C")).MetricNames);
        Assert.All(messages.Where(x => x.Kind == "DDATA"), x => Assert.Equal("Disk_C", x.DeviceId));
        // One warning for the device and one for the metric, however often they are published.
        Assert.Equal(2, log.Count("{What} '{Name}' is not a valid Ignition name; publishing it as"));
        Assert.Equal(4.0, publisher.GetDeviceMetric("Disk.C", "Usage %")!.Value); // callers keep using their own names
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task RenameOff_AnInvalidMetricOrDevice_IsLeftOut_AndTheRestIsPublished()
    {
        await using var broker = await TestBroker.StartAsync();
        var config = broker.Config();
        config.RenameInvalidNames = false;
        var (publisher, log) = Create(config);
        using var _ = publisher;

        await publisher.PublishDeviceBirthAsync("CPU", new[] { M("Load", 1), M("Usage %", 1) });
        await publisher.PublishDeviceBirthAsync("Disk.C", new[] { M("Used", 1) });
        await publisher.ConnectAsync();
        await publisher.PublishDeviceMetricsAsync("CPU", new[] { M("Load", 2), M("Usage %", 2) });
        await publisher.PublishDeviceMetricsAsync("CPU", new[] { M("Load", 3), M("Usage %", 3) });
        await publisher.PublishDeviceMetricsAsync("Disk.C", new[] { M("Used", 2) });

        var messages = await broker.WaitForAsync(m => m.Count(x => x.Kind == "DDATA") == 2, "two DDATA for CPU");
        await Task.Delay(200);

        Assert.Equal(new[] { "Load" }, messages.Single(x => IsBirth(x, "DBIRTH", "CPU")).MetricNames);
        Assert.All(broker.Messages.Where(x => x.Kind == "DDATA"), x => Assert.Equal(new[] { "Load" }, x.MetricNames));
        Assert.DoesNotContain(broker.Messages, x => x.DeviceId is not null && x.DeviceId != "CPU");
        Assert.Equal(2, log.Count("{What} '{Name}' is not a valid Ignition name (")); // metric and device, once each
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task RenameOff_AnInvalidGroupOrEdgeNodeId_IsAConfigurationError()
    {
        await using var broker = await TestBroker.StartAsync();

        var badGroup = broker.Config();
        badGroup.RenameInvalidNames = false;
        badGroup.GroupId = "Site.1";
        Assert.Throws<ArgumentException>(() => new SparkplugPublisher(badGroup));

        // An application may set the edge node ID after constructing the publisher.
        var config = broker.Config();
        config.RenameInvalidNames = false;
        using var publisher = new SparkplugPublisher(config);
        config.EdgeNodeId = "host.domain.local";
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.ConnectAsync());
    }

    [Fact]
    public async Task RenameOn_AnEdgeNodeIdSetAfterConstruction_IsPublishedUnderItsValidForm()
    {
        await using var broker = await TestBroker.StartAsync();
        var config = broker.Config();
        using var publisher = new SparkplugPublisher(config);
        config.EdgeNodeId = "host.domain.local";

        await publisher.ConnectAsync();

        var messages = await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");
        Assert.Equal("spBv1.0/G/NBIRTH/host_domain_local", messages.First(x => x.Kind == "NBIRTH").Topic);
    }

    [Fact]
    public async Task TwoNamesThatBecomeTheSameName_TheSecondIsLeftOut()
    {
        await using var broker = await TestBroker.StartAsync();
        var (publisher, log) = Create(broker.Config());
        using var _ = publisher;

        await publisher.PublishDeviceBirthAsync("A", new[] { M("Temp.1", 10) });
        await publisher.ConnectAsync();
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Temp_1", 99) }); // would merge into Temp.1's tag
        await publisher.PublishDeviceMetricsAsync("A", new[] { M("Temp.1", 11) });

        await broker.WaitForAsync(m => m.Any(x => x.Kind == "DDATA"), "DDATA for Temp.1");
        await Task.Delay(200);

        Assert.All(broker.Messages.Where(x => x.Kind == "DDATA"), x => Assert.Equal(11.0, x.Metric("Temp_1").DoubleValue));
        Assert.Equal(1, log.Count("{What} '{Name}' would be published as"));
        SparkplugAssert.SessionsAreWellFormed(broker.Messages);
    }

    [Fact]
    public async Task UnusableBdSeqFile_IsLogged_AndTheNodeStillPublishes()
    {
        await using var broker = await TestBroker.StartAsync();

        // Unreadable content: logged, and the sequence starts at 0.
        File.WriteAllText(broker.BdSeqFile, "garbage");
        var (reader, readLog) = Create(broker.Config());
        using (reader)
        {
            await reader.ConnectAsync();
            var births = await broker.WaitForAsync(m => m.Any(x => x.Kind == "NBIRTH"), "NBIRTH");
            Assert.Equal(0UL, births.First(x => x.Kind == "NBIRTH").BdSeq);
            Assert.Equal(1, readLog.Count("Could not read the bdSeq state file"));
            await reader.DisconnectAsync();
        }

        // Unwritable location: logged, and the node publishes anyway.
        var blocker = broker.BdSeqFile + ".blocker";
        File.WriteAllText(blocker, "a file where a folder is needed");
        var config = broker.Config();
        config.BdSeqFilePath = Path.Combine(blocker, "bdseq.txt");
        var (writer, writeLog) = Create(config);
        using (writer)
        {
            await writer.ConnectAsync();
            await broker.WaitForAsync(m => m.Count(x => x.Kind == "NBIRTH") == 2, "second NBIRTH");
            Assert.Equal(1, writeLog.Count("Could not save the bdSeq state file"));
        }
    }
}
