using Com.Cirruslink.Sparkplug.Protobuf;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using Serilog;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// A Sparkplug B edge node: one MQTT connection, the birth and death certificates, and the
    /// sequence numbering the specification requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sessions.</b> Every MQTT connection is a new Sparkplug session. Its CONNECT registers an NDEATH
    /// will carrying a new bdSeq (0-255, one more than the previous connection's, persisted across
    /// restarts). Immediately afterwards the node publishes NBIRTH (seq 0) and a DBIRTH for every known
    /// device, before any data (tck-id-payloads-dbirth-order, tck-id-payloads-ddata-order). The publisher
    /// reconnects by itself, with back-off, and repeats the birth sequence on each new connection.
    /// </para>
    /// <para>
    /// <b>Sequence numbers.</b> One counter for the node, shared by every message except NDEATH and
    /// allocated under the same lock as the publish, so messages leave in seq order and nothing can slip
    /// into a birth sequence (tck-id-payloads-sequence-num-incrementing,
    /// tck-id-operational-behavior-data-commands-rebirth-action-1).
    /// </para>
    /// <para>
    /// <b>Declared metrics.</b> Data may only carry metrics its birth declared
    /// (tck-id-operational-behavior-data-publish-nbirth / -dbirth). A metric name or data type that has
    /// not been declared on this connection triggers a full NBIRTH + DBIRTH sequence that carries it, and
    /// data for a device not yet birthed on this connection publishes that device's DBIRTH first.
    /// </para>
    /// <para>
    /// <b>While disconnected</b>, publish calls do not throw. They update the stored values, which the
    /// next birth sequence carries. A value that cannot be encoded is rejected before it is stored.
    /// </para>
    /// </remarks>
    public class SparkplugPublisher : ISparkplugPublisher
    {
        private const string RebirthMetricName = "Node Control/Rebirth";
        private static readonly TimeSpan NodeDeathTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

        private readonly SparkplugConfiguration _config;
        private readonly IMqttClient _client;
        private readonly MetricStore _metricStore = new();
        private readonly BdSeqManager _bdSeqManager;
        private ILogger _logger;

        // Serialises sequence-number allocation, publishing, and the session state below.
        private readonly SemaphoreSlim _publishLock = new(1, 1);

        // ---- Session state. Guarded by _publishLock, except that the connection supervisor writes the
        // bdSeq fields between CONNACK and the birth sequence, when no birth can be in progress. ----
        private ulong? _lastBdSeq;   // bdSeq of the most recent CONNECT the broker accepted
        private ulong _bdSeq;        // bdSeq of the current connection: its will, its NBIRTHs, the explicit NDEATH
        private ulong _seq;          // next sequence number, 0-255
        private volatile bool _sessionBirthed; // the current connection has completed a birth sequence
        private readonly Dictionary<string, MetricDataType> _declaredNodeMetrics = new();
        private readonly Dictionary<string, Dictionary<string, MetricDataType>> _declaredDeviceMetrics = new();

        // ---- Connection supervision ----
        private readonly object _lifecycleLock = new();
        private readonly SemaphoreSlim _wakeSupervisor = new(0);
        private CancellationTokenSource? _supervisorCts;
        private Task? _supervisor;
        private TaskCompletionSource? _firstAttempt;
        private volatile bool _stopping;
        private volatile bool _disposed;

        public bool IsConnected => _client.IsConnected;

        public SparkplugPublisher(SparkplugConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid configuration: {error}", nameof(config));
            }

            _logger = Log.Logger; // Default to global logger
            _bdSeqManager = new BdSeqManager(_config.BdSeqFilePath);
            _lastBdSeq = _bdSeqManager.LoadLast();
            _logger.Information("bdSeq state {Path}: last connection used {BdSeq}",
                _bdSeqManager.FilePath, _lastBdSeq?.ToString() ?? "none");

            _client = new MqttFactory().CreateMqttClient();
            _client.DisconnectedAsync += OnDisconnectedAsync;
            _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        }

        public void SetLogger(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // ================================================================================================
        // Connection lifecycle
        // ================================================================================================

        /// <summary>
        /// Starts connecting to the broker and returns once the first attempt has finished. If it fails,
        /// the publisher keeps retrying in the background with back-off and publishes the birth sequence
        /// whenever a connection is made, so an unreachable broker does not make this throw. Calling it
        /// again while the publisher is running has no effect.
        /// </summary>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            Task firstAttempt;
            lock (_lifecycleLock)
            {
                if (_supervisor is null)
                {
                    _stopping = false;
                    _supervisorCts = new CancellationTokenSource();
                    _firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var token = _supervisorCts.Token;
                    _logger.Information("Connecting to MQTT broker {Broker}:{Port}", _config.MqttBroker, _config.MqttPort);
                    _supervisor = Task.Run(() => SuperviseConnectionAsync(token));
                }

                firstAttempt = _firstAttempt!.Task;
            }

            return firstAttempt.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Stops the publisher: publishes NDEATH (a clean MQTT DISCONNECT makes the broker discard the
        /// will, so it has to be sent explicitly) and disconnects. Does not throw for connection problems.
        /// </summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Task? supervisor;
            CancellationTokenSource? supervisorCts;
            lock (_lifecycleLock)
            {
                _stopping = true;
                supervisor = _supervisor;
                supervisorCts = _supervisorCts;
                _supervisor = null;
                _supervisorCts = null;
            }

            if (supervisor != null)
            {
                supervisorCts!.Cancel();
                await supervisor.ConfigureAwait(false);
                supervisorCts.Dispose();
            }

            await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _sessionBirthed = false;

                if (_client.IsConnected)
                {
                    _logger.Information("Disconnecting from MQTT broker");

                    // tck-id-payloads-ndeath-will-message-publisher-disconnect-mqtt311
                    await PublishNodeDeathUnderLockAsync(cancellationToken).ConfigureAwait(false);

                    try
                    {
                        using var timeout = new CancellationTokenSource(NodeDeathTimeout);
                        await _client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Ignoring error while disconnecting");
                    }

                    _logger.Information("Disconnected from MQTT broker");
                }
            }
            finally
            {
                _publishLock.Release();
            }
        }

        /// <summary>
        /// Keeps a birthed session up: connects (with back-off on failure), publishes the birth sequence,
        /// then sleeps until the connection drops or a birth sequence is interrupted.
        /// </summary>
        private async Task SuperviseConnectionAsync(CancellationToken cancellationToken)
        {
            var minDelay = TimeSpan.FromSeconds(_config.ReconnectMinDelaySeconds);
            var maxDelay = TimeSpan.FromSeconds(_config.ReconnectMaxDelaySeconds);
            var delay = minDelay;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!SessionIsLive)
                {
                    var ok = _client.IsConnected
                        ? await TryStartSessionAsync(cancellationToken).ConfigureAwait(false) // connected, births incomplete
                        : await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                    _firstAttempt?.TrySetResult();

                    if (!ok)
                    {
                        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }

                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
                        continue;
                    }

                    delay = minDelay;
                }

                try { await _wakeSupervisor.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            _firstAttempt?.TrySetResult();
        }

        private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
        {
            var bdSeq = BdSeqManager.Next(_lastBdSeq);

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(_config.ConnectTimeoutSeconds));
                await _client.ConnectAsync(BuildClientOptions(bdSeq), attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning("Could not connect to MQTT broker {Broker}:{Port}: {Reason}",
                    _config.MqttBroker, _config.MqttPort, ex.Message);
                _logger.Debug(ex, "Connection attempt failed");
                return false;
            }

            // The broker accepted the CONNECT, so its will now carries this bdSeq. Only an accepted
            // connection uses up a bdSeq: failed attempts would otherwise leave gaps in the sequence.
            _lastBdSeq = bdSeq;
            _bdSeq = bdSeq;
            _bdSeqManager.Save(bdSeq);
            _logger.Information("Connected to MQTT broker {Broker}:{Port} with bdSeq {BdSeq}",
                _config.MqttBroker, _config.MqttPort, bdSeq);

            return await TryStartSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Subscribes to NCMD and publishes the birth sequence on the current connection.</summary>
        private async Task<bool> TryStartSessionAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Subscribe before the NBIRTH, so a rebirth request is never missed.
                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f
                        .WithTopic(SparkplugTopics.GetNodeCommandTopic(_config.GroupId, _config.EdgeNodeId))
                        .WithAtLeastOnceQoS()) // tck-id-message-flow-edge-node-ncmd-subscribe
                    .Build(), cancellationToken).ConfigureAwait(false);

                await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _publishLock.Release();
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning("Connected to the MQTT broker, but the birth sequence failed: {Reason}", ex.Message);
                _logger.Debug(ex, "Birth sequence failed");
                return false;
            }
        }

        private MqttClientOptions BuildClientOptions(ulong bdSeq)
        {
            var builder = new MqttClientOptionsBuilder()
                .WithClientId($"{_config.GroupId}.{_config.EdgeNodeId}")
                .WithTcpServer(_config.MqttBroker, _config.MqttPort)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
                .WithTimeout(TimeSpan.FromSeconds(_config.ConnectTimeoutSeconds))
                .WithCleanSession(true)
                // The will is this connection's NDEATH, published by the broker if the connection drops
                // without a clean DISCONNECT. QoS 1, not retained (tck-id-payloads-ndeath-will-message-qos / -retain).
                .WithWillTopic(SparkplugTopics.GetNodeDeathTopic(_config.GroupId, _config.EdgeNodeId))
                .WithWillPayload(SparkplugMessageFactory.CreateNodeDeath(bdSeq))
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithWillRetain(false);

            if (!string.IsNullOrEmpty(_config.Username))
            {
                builder.WithCredentials(_config.Username, _config.Password);
            }

            if (_config.UseTls)
            {
                builder.WithTlsOptions(o => o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12));
            }

            return builder.Build();
        }

        private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            _sessionBirthed = false;

            if (e.ClientWasConnected && !_stopping)
            {
                _logger.Warning("Disconnected from MQTT broker ({Reason}); reconnecting", e.Reason);
            }

            WakeSupervisor();
            return Task.CompletedTask;
        }

        private void WakeSupervisor()
        {
            try { _wakeSupervisor.Release(); }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        private bool SessionIsLive => _sessionBirthed && _client.IsConnected;

        // ================================================================================================
        // Publishing
        // ================================================================================================

        public async Task PublishNodeMetricsAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            var list = ToValidatedList(metrics);
            if (list.Count == 0) return;

            await RunUnderLockAsync(async () =>
            {
                _metricStore.UpdateNodeMetrics(list);
                if (!SessionIsLive) return;

                if (!AllDeclared(_declaredNodeMetrics, list))
                {
                    _logger.Information("Node metric(s) {Names} not declared in the NBIRTH; publishing a new birth sequence",
                        Undeclared(_declaredNodeMetrics, list));
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var sequence = NextSeq();
                await PublishUnderLockAsync(SparkplugTopics.GetNodeDataTopic(_config.GroupId, _config.EdgeNodeId),
                    SparkplugMessageFactory.CreatePayload(list, sequence), cancellationToken).ConfigureAwait(false);
                _logger.Debug("Published NDATA with {MetricCount} metrics, sequence {Sequence}", list.Count, sequence);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Declares the node's full metric set and, when connected, publishes a new birth sequence:
        /// an NBIRTH resets the session for the host, so every DBIRTH follows it.
        /// </summary>
        public async Task PublishNodeBirthAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken)
        {
            var list = ToValidatedList(metrics);

            await RunUnderLockAsync(async () =>
            {
                _metricStore.ReplaceNodeMetrics(list);
                if (SessionIsLive)
                {
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Declares a device's full metric set and, when connected, publishes its DBIRTH.
        /// </summary>
        public async Task PublishDeviceBirthAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);
            var list = ToValidatedList(metrics);

            await RunUnderLockAsync(async () =>
            {
                _metricStore.ReplaceDeviceMetrics(deviceId, list);
                if (SessionIsLive)
                {
                    await PublishDeviceBirthUnderLockAsync(deviceId, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task PublishDeviceMetricsAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);
            var list = ToValidatedList(metrics);
            if (list.Count == 0) return;

            await RunUnderLockAsync(async () =>
            {
                _metricStore.UpdateDeviceMetrics(deviceId, list);
                if (!SessionIsLive) return;

                if (!_declaredDeviceMetrics.TryGetValue(deviceId, out var declared))
                {
                    _logger.Information("Device {DeviceId} has not been birthed on this connection; publishing its DBIRTH", deviceId);
                    await PublishDeviceBirthUnderLockAsync(deviceId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!AllDeclared(declared, list))
                {
                    _logger.Information("Metric(s) {Names} not declared in the DBIRTH for {DeviceId}; publishing a new birth sequence",
                        Undeclared(declared, list), deviceId);
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var sequence = NextSeq();
                await PublishUnderLockAsync(SparkplugTopics.GetDeviceDataTopic(_config.GroupId, _config.EdgeNodeId, deviceId),
                    SparkplugMessageFactory.CreatePayload(list, sequence), cancellationToken).ConfigureAwait(false);
                _logger.Debug("Published DDATA for device {DeviceId} with {MetricCount} metrics, sequence {Sequence}",
                    deviceId, list.Count, sequence);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks a device offline. It is forgotten, so later connections do not birth it again; publishing
        /// data or a DBIRTH for it brings it back. A second call for the same device does nothing.
        /// </summary>
        public async Task PublishDeviceDeathAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);

            await RunUnderLockAsync(async () =>
            {
                var wasBirthed = _declaredDeviceMetrics.Remove(deviceId);
                _metricStore.RemoveDevice(deviceId);

                // Offline, the connection's NDEATH has already told the host the device is gone.
                if (!wasBirthed || !SessionIsLive) return;

                var sequence = NextSeq();
                await PublishUnderLockAsync(SparkplugTopics.GetDeviceDeathTopic(_config.GroupId, _config.EdgeNodeId, deviceId),
                    SparkplugMessageFactory.CreateDeviceDeath(sequence), cancellationToken).ConfigureAwait(false);
                _logger.Information("Published DDEATH for device {DeviceId}, sequence {Sequence}", deviceId, sequence);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes only metrics that have changed since last update
        /// </summary>
        public async Task PublishChangedMetricsAsync(CancellationToken cancellationToken = default)
        {
            var (nodeMetrics, deviceMetrics) = _metricStore.GetChangedMetrics();

            var changedNode = nodeMetrics.ToList();
            if (changedNode.Count > 0)
            {
                await PublishNodeMetricsAsync(changedNode, cancellationToken).ConfigureAwait(false);
            }

            foreach (var kvp in deviceMetrics)
            {
                var changed = kvp.Value.ToList();
                if (changed.Count > 0)
                {
                    await PublishDeviceMetricsAsync(kvp.Key, changed, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Republishes the full birth sequence (NBIRTH with the connection's bdSeq, then every DBIRTH), as a
        /// Node Control/Rebirth request requires. Does nothing while disconnected: the next connection
        /// publishes the births anyway.
        /// </summary>
        public async Task HandleRebirthAsync(CancellationToken cancellationToken = default)
        {
            await RunUnderLockAsync(async () =>
            {
                // Reset change tracking to ensure all metrics are sent
                _metricStore.ResetChangeTracking();

                if (SessionIsLive)
                {
                    _logger.Information("Handling rebirth request");
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the current value of a node metric
        /// </summary>
        public Metric? GetNodeMetric(string metricName)
        {
            return _metricStore.GetNodeMetric(metricName);
        }

        /// <summary>
        /// Gets the current value of a device metric
        /// </summary>
        public Metric? GetDeviceMetric(string deviceId, string metricName)
        {
            return _metricStore.GetDeviceMetric(deviceId, metricName);
        }

        /// <summary>
        /// Gets all current node metrics
        /// </summary>
        public IEnumerable<Metric> GetAllNodeMetrics()
        {
            return _metricStore.GetNodeMetrics();
        }

        /// <summary>
        /// Gets all current device metrics
        /// </summary>
        public IEnumerable<Metric> GetAllDeviceMetrics(string deviceId)
        {
            return _metricStore.GetDeviceMetrics(deviceId);
        }

        // ================================================================================================
        // Under the publish lock
        // ================================================================================================

        /// <summary>
        /// Runs a publish operation under the lock. A connection lost part way is not an error for the
        /// caller (the values are stored, and the next connection's births carry them); anything else is
        /// rethrown. An interrupted birth sequence wakes the supervisor to redo it.
        /// </summary>
        private async Task RunUnderLockAsync(Func<Task> operation, CancellationToken cancellationToken)
        {
            await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MqttCommunicationException or ObjectDisposedException)
            {
                _logger.Debug("Publish interrupted by the connection dropping: {Reason}", ex.Message);
                _sessionBirthed = false;
                WakeSupervisor();
            }
            catch
            {
                if (!_sessionBirthed) WakeSupervisor();
                throw;
            }
            finally
            {
                _publishLock.Release();
            }
        }

        /// <summary>
        /// NBIRTH (seq 0, this connection's bdSeq) followed by a DBIRTH for every known device. Until it
        /// completes the session is not live, so no data can be published in between.
        /// </summary>
        private async Task PublishBirthSequenceUnderLockAsync(CancellationToken cancellationToken)
        {
            _sessionBirthed = false;
            _declaredNodeMetrics.Clear();
            _declaredDeviceMetrics.Clear();
            _seq = 0;

            var nodeMetrics = _metricStore.GetNodeMetrics().ToList();
            await PublishUnderLockAsync(SparkplugTopics.GetNodeBirthTopic(_config.GroupId, _config.EdgeNodeId),
                SparkplugMessageFactory.CreateNodeBirth(nodeMetrics, _bdSeq, NextSeq()), cancellationToken).ConfigureAwait(false);
            foreach (var metric in nodeMetrics)
            {
                _declaredNodeMetrics[metric.Name] = metric.DataType;
            }
            _logger.Information("Published NBIRTH with bdSeq {BdSeq} and {MetricCount} metrics", _bdSeq, nodeMetrics.Count);

            foreach (var deviceId in _metricStore.GetDeviceIds().OrderBy(id => id, StringComparer.Ordinal))
            {
                await PublishDeviceBirthUnderLockAsync(deviceId, cancellationToken).ConfigureAwait(false);
            }

            _sessionBirthed = true;
        }

        private async Task PublishDeviceBirthUnderLockAsync(string deviceId, CancellationToken cancellationToken)
        {
            var metrics = _metricStore.GetDeviceMetrics(deviceId).ToList();
            var sequence = NextSeq();
            await PublishUnderLockAsync(SparkplugTopics.GetDeviceBirthTopic(_config.GroupId, _config.EdgeNodeId, deviceId),
                SparkplugMessageFactory.CreatePayload(metrics, sequence), cancellationToken).ConfigureAwait(false);

            _declaredDeviceMetrics[deviceId] = metrics.ToDictionary(m => m.Name, m => m.DataType);
            _logger.Information("Published DBIRTH for device {DeviceId} with {MetricCount} metrics, sequence {Sequence}",
                deviceId, metrics.Count, sequence);
        }

        /// <summary>
        /// NDEATH with this connection's bdSeq, QoS 1 like the will, so the PUBACK confirms it arrived
        /// before the DISCONNECT. Bounded by <see cref="NodeDeathTimeout"/> so a dead broker cannot stall
        /// shutdown.
        /// </summary>
        private async Task PublishNodeDeathUnderLockAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NodeDeathTimeout);

            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(SparkplugTopics.GetNodeDeathTopic(_config.GroupId, _config.EdgeNodeId))
                    .WithPayload(SparkplugMessageFactory.CreateNodeDeath(_bdSeq))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _client.PublishAsync(message, timeout.Token).ConfigureAwait(false);
                _logger.Information("Published NDEATH with bdSeq {BdSeq}", _bdSeq);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to publish NDEATH with bdSeq {BdSeq}; the host may show the node online", _bdSeq);
            }
        }

        /// <summary>
        /// Births, data and DDEATH are QoS 0 and not retained (tck-id-topics-*-mqtt): no message from one
        /// session can be redelivered into another.
        /// </summary>
        private async Task PublishUnderLockAsync(string topic, byte[] payload, CancellationToken cancellationToken)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The next sequence number: one counter per node, wrapping from 255 to 0.</summary>
        private ulong NextSeq()
        {
            var value = _seq;
            _seq = (_seq + 1) % 256;
            return value;
        }

        // ================================================================================================
        // Commands
        // ================================================================================================

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            if (e.ApplicationMessage.Topic != SparkplugTopics.GetNodeCommandTopic(_config.GroupId, _config.EdgeNodeId))
            {
                return Task.CompletedTask;
            }

            try
            {
                var payload = Payload.Parser.ParseFrom(e.ApplicationMessage.PayloadSegment);

                // A rebirth request names 'Node Control/Rebirth' with the value true. The datatype is not
                // checked: it SHOULD NOT be sent in NCMD (tck-id-payloads-metric-datatype-not-req).
                var rebirthRequested = payload.Metrics.Any(m =>
                    m.Name == RebirthMetricName &&
                    m.ValueCase == Payload.Types.Metric.ValueOneofCase.BooleanValue &&
                    m.BooleanValue);

                if (rebirthRequested)
                {
                    _logger.Information("Rebirth requested via NCMD");

                    // Off the MQTT receive loop: the rebirth waits for the publish lock.
                    _ = Task.Run(async () =>
                    {
                        try { await HandleRebirthAsync().ConfigureAwait(false); }
                        catch (Exception ex) { _logger.Error(ex, "Failed to handle rebirth request"); }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to handle NCMD payload");
            }

            return Task.CompletedTask;
        }

        // ================================================================================================
        // Helpers
        // ================================================================================================

        private static List<Metric> ToValidatedList(IEnumerable<Metric> metrics)
        {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));

            var list = metrics.ToList();
            SparkplugMessageFactory.Validate(list);
            return list;
        }

        private static bool AllDeclared(Dictionary<string, MetricDataType> declared, List<Metric> metrics) =>
            metrics.All(m => declared.TryGetValue(m.Name, out var type) && type == m.DataType);

        private static string Undeclared(Dictionary<string, MetricDataType> declared, List<Metric> metrics) =>
            string.Join(", ", metrics
                .Where(m => !declared.TryGetValue(m.Name, out var type) || type != m.DataType)
                .Select(m => m.Name));

        private static void ValidateDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SparkplugPublisher));
        }

        /// <summary>
        /// Stops the publisher if it is still running (publishing NDEATH, as DisconnectAsync does) and
        /// releases the MQTT client.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!DisconnectAsync().Wait(DisposeTimeout))
                {
                    _logger.Warning("Timed out stopping the Sparkplug publisher during Dispose");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error disposing SparkplugPublisher");
            }
            finally
            {
                _client.Dispose();
            }
        }
    }
}
