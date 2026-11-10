using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Com.Cirruslink.Sparkplug.Protobuf;
using Google.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace SparkplugB.Publisher.Tests.Infrastructure;

/// <summary>One Sparkplug message as the host application received it.</summary>
public sealed record HostMessage(int Index, string Topic, string Kind, string? DeviceId, MqttQualityOfServiceLevel Qos, Payload Payload)
{
    public ulong? Seq => Payload.HasSeq ? Payload.Seq : null;

    public ulong? BdSeq => Payload.Metrics.FirstOrDefault(m => m.Name == "bdSeq")?.LongValue;

    public IReadOnlyList<string> MetricNames => Payload.Metrics.Select(m => m.Name).ToList();

    public Payload.Types.Metric Metric(string name) => Payload.Metrics.Single(m => m.Name == name);

    public IReadOnlyList<Payload.Types.Metric> HistoricalMetrics => Payload.Metrics.Where(m => m.IsHistorical).ToList();

    public override string ToString() =>
        $"#{Index} {Kind}{(DeviceId is null ? "" : "/" + DeviceId)} seq={Seq?.ToString() ?? "-"} " +
        $"bdSeq={BdSeq?.ToString() ?? "-"} qos={(int)Qos} [{string.Join(", ", MetricNames)}]";
}

/// <summary>
/// An in-process MQTT broker with a TCP proxy in front of it and a recording Sparkplug host.
/// The publisher under test connects to the proxy, so a test can cut the connection (network loss,
/// broker publishes the will) or stop listening (broker unreachable) without touching the broker.
/// </summary>
public sealed class TestBroker : IAsyncDisposable
{
    public const string Group = "G";
    public const string Node = "N";

    private readonly MqttServer _server;
    private readonly IMqttClient _host;
    private readonly Proxy _proxy;
    private readonly List<HostMessage> _messages = new();
    private readonly object _gate = new();
    private readonly string _stateDirectory;

    private TestBroker(MqttServer server, IMqttClient host, Proxy proxy, int proxyPort)
    {
        _server = server;
        _host = host;
        _proxy = proxy;
        ProxyPort = proxyPort;
        _stateDirectory = Path.Combine(Path.GetTempPath(), "jpi-spb-lib-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);
    }

    public int ProxyPort { get; }

    /// <summary>bdSeq state file for this test, isolated from every other test.</summary>
    public string BdSeqFile => Path.Combine(_stateDirectory, "bdseq.txt");

    public static async Task<TestBroker> StartAsync(bool proxyListening = true)
    {
        var brokerPort = FreePort();
        var proxyPort = FreePort();
        var factory = new MqttFactory();

        var server = factory.CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(brokerPort)
            .Build());
        await server.StartAsync();

        var broker = new TestBroker(server, factory.CreateMqttClient(), new Proxy(proxyPort, brokerPort), proxyPort);
        await broker.ConnectHostAsync(brokerPort);
        if (proxyListening)
        {
            broker.StartProxy();
        }
        return broker;
    }

    /// <summary>Publisher configuration pointing at the proxy, with fast reconnects for tests.</summary>
    public SparkplugConfiguration Config() => new()
    {
        GroupId = Group,
        EdgeNodeId = Node,
        MqttBroker = "127.0.0.1",
        MqttPort = ProxyPort,
        KeepAliveSeconds = 5,
        ReconnectMinDelaySeconds = 0.1,
        ReconnectMaxDelaySeconds = 0.5,
        ConnectTimeoutSeconds = 2,
        BdSeqFilePath = BdSeqFile,
    };

    /// <summary>Everything the host has received so far, in arrival order.</summary>
    public IReadOnlyList<HostMessage> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    /// <summary>Drops every proxied connection abruptly, as a network failure would.</summary>
    public void Cut() => _proxy.Cut();

    public void StartProxy() => _proxy.Start();

    public void StopProxy() => _proxy.Stop();

    /// <summary>Sends a 'Node Control/Rebirth' NCMD, as a host application does.</summary>
    public async Task SendRebirthRequestAsync(bool includeDatatype)
    {
        var metric = new Payload.Types.Metric { Name = "Node Control/Rebirth", BooleanValue = true };
        if (includeDatatype)
        {
            metric.Datatype = (uint)MetricDataType.Boolean;
        }

        var payload = new Payload { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        payload.Metrics.Add(metric);

        await _host.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"spBv1.0/{Group}/NCMD/{Node}")
            .WithPayload(payload.ToByteArray())
            .Build());
    }

    /// <summary>Polls the received messages until the condition holds, or fails with a trace.</summary>
    public async Task<IReadOnlyList<HostMessage>> WaitForAsync(
        Func<IReadOnlyList<HostMessage>, bool> condition, string description, int timeoutMs = 8000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var snapshot = Messages;
            if (condition(snapshot))
            {
                return snapshot;
            }
            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Timed out after {timeoutMs} ms waiting for {description}. The host saw:\n" +
            string.Join("\n", Messages));
    }

    private async Task ConnectHostAsync(int brokerPort)
    {
        _host.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var parts = topic.Split('/');
            var payload = Payload.Parser.ParseFrom(e.ApplicationMessage.PayloadSegment);
            lock (_gate)
            {
                _messages.Add(new HostMessage(_messages.Count, topic, parts[2], parts.Length > 4 ? parts[4] : null,
                    e.ApplicationMessage.QualityOfServiceLevel, payload));
            }
            return Task.CompletedTask;
        };

        await _host.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", brokerPort)
            .WithClientId("test-host-" + Guid.NewGuid().ToString("N"))
            .Build());

        // QoS 1 subscription: the delivered QoS is then the QoS the publisher used (0 or 1).
        await _host.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic("spBv1.0/#").WithAtLeastOnceQoS())
            .Build());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _proxy.Stop();
        try { await _host.DisconnectAsync(); } catch { /* broker may already be gone */ }
        _host.Dispose();
        await _server.StopAsync(new MqttServerStopOptions());
        _server.Dispose();
        try { Directory.Delete(_stateDirectory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Forwards TCP between the publisher and the broker, and can sever it.</summary>
    private sealed class Proxy
    {
        private readonly int _listenPort;
        private readonly int _targetPort;
        private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
        private TcpListener? _listener;

        public Proxy(int listenPort, int targetPort)
        {
            _listenPort = listenPort;
            _targetPort = targetPort;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _listener.Start();
            _ = AcceptLoopAsync(_listener);
        }

        public void Stop()
        {
            _listener?.Stop();
            _listener = null;
            Cut();
        }

        public void Cut()
        {
            foreach (var connection in _connections.Keys)
            {
                try
                {
                    connection.Client.LingerState = new LingerOption(true, 0); // RST, not FIN
                    connection.Close();
                }
                catch { /* already closed */ }
            }
            _connections.Clear();
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient inbound;
                try { inbound = await listener.AcceptTcpClientAsync(); }
                catch { return; } // listener stopped

                var outbound = new TcpClient();
                try { await outbound.ConnectAsync(IPAddress.Loopback, _targetPort); }
                catch { inbound.Close(); continue; }

                _connections[inbound] = 0;
                _connections[outbound] = 0;
                _ = PumpAsync(inbound, outbound);
                _ = PumpAsync(outbound, inbound);
            }
        }

        private static async Task PumpAsync(TcpClient from, TcpClient to)
        {
            try { await from.GetStream().CopyToAsync(to.GetStream()); }
            catch { /* connection cut */ }
            finally
            {
                try { from.Close(); to.Close(); } catch { /* already closed */ }
            }
        }
    }
}
