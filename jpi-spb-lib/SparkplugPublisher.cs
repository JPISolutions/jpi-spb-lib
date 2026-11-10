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

        // Hosts reject a whole payload when one metric in it is bad (seen with Ignition MQTT Engine), so a
        // backfill is split into messages of at most this many metrics.
        private const int BackfillMaxMetricsPerMessage = 500;

        private static readonly TimeSpan NodeDeathTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

        private readonly SparkplugConfiguration _config;
        private readonly IMqttClient _client;
        private readonly MetricStore _metricStore = new();
        private readonly NameResolver _names;
        private ILogger _logger;

        // Group and edge node IDs as published (renamed when invalid), and the bdSeq state for that edge
        // node. Resolved by ConnectAsync, since a caller may set the IDs after constructing the publisher.
        private string _groupId;
        private string _edgeNodeId;
        private BdSeqManager? _bdSeqManager;

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

        // ---- History backfill (null when disabled), guarded by _publishLock ----
        private readonly BackfillBuffer? _backfill;
        private bool _backfillOverflowLogged; // re-armed once a reconnect has emptied the buffer

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
            _names = new NameResolver(_config.RenameInvalidNames, () => _logger);
            _groupId = SparkplugNames.MakeValidId(_config.GroupId);
            _edgeNodeId = SparkplugNames.MakeValidId(_config.EdgeNodeId);

            if (_config.BackfillEnabled)
            {
                _backfill = new BackfillBuffer(_config.BackfillMaxSamples);
                _logger.Information("History backfill enabled: up to {MaxSamples} samples are kept while disconnected",
                    _config.BackfillMaxSamples);
            }

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
        /// <exception cref="ArgumentException">
        /// The configuration is invalid, for example an EdgeNodeId set after construction that is not a
        /// valid Ignition name while RenameInvalidNames is off.
        /// </exception>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            Task firstAttempt;
            lock (_lifecycleLock)
            {
                if (_supervisor is null)
                {
                    // Checked again here: a caller may have changed the configuration since construction.
                    if (!_config.Validate(out var error))
                    {
                        throw new ArgumentException($"Invalid configuration: {error}");
                    }

                    _groupId = ResolveConfiguredId(nameof(_config.GroupId), _config.GroupId);
                    _edgeNodeId = ResolveConfiguredId(nameof(_config.EdgeNodeId), _config.EdgeNodeId);

                    _bdSeqManager = new BdSeqManager(_config.BdSeqFilePath, _groupId, _edgeNodeId);
                    _lastBdSeq = _bdSeqManager.LoadLast(out var problem);
                    if (problem != null)
                    {
                        _logger.Warning("Could not read the bdSeq state file {Path}: {Problem}; the bdSeq sequence starts again at 0",
                            _bdSeqManager.FilePath, problem);
                    }
                    _logger.Information("bdSeq state {Path}: last connection used {BdSeq}",
                        _bdSeqManager.FilePath, _lastBdSeq?.ToString() ?? "none");

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
            if (!_bdSeqManager!.TrySave(bdSeq, out var problem))
            {
                _logger.Warning("Could not save the bdSeq state file {Path}: {Problem}; after a restart the bdSeq sequence may repeat",
                    _bdSeqManager.FilePath, problem);
            }
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
                        .WithTopic(SparkplugTopics.GetNodeCommandTopic(_groupId, _edgeNodeId))
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
                .WithClientId($"{_groupId}.{_edgeNodeId}")
                .WithTcpServer(_config.MqttBroker, _config.MqttPort)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
                .WithTimeout(TimeSpan.FromSeconds(_config.ConnectTimeoutSeconds))
                .WithCleanSession(true)
                // The will is this connection's NDEATH, published by the broker if the connection drops
                // without a clean DISCONNECT. QoS 1, not retained (tck-id-payloads-ndeath-will-message-qos / -retain).
                .WithWillTopic(SparkplugTopics.GetNodeDeathTopic(_groupId, _edgeNodeId))
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

        public Task PublishNodeMetricsAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken = default) =>
            PublishNodeDataAsync(ToValidatedList(_names.ResolveMetrics(null, metrics)), cancellationToken);

        private async Task PublishNodeDataAsync(List<Metric> list, CancellationToken cancellationToken)
        {
            if (list.Count == 0) return;

            await RunUnderLockAsync(async () =>
            {
                _metricStore.UpdateNodeMetrics(list);
                if (!SessionIsLive)
                {
                    BufferForBackfill(null, list);
                    return;
                }

                var carriedByBirth = false;
                if (!AllDeclared(_declaredNodeMetrics, list))
                {
                    _logger.Information("Node metric(s) {Names} not declared in the NBIRTH; publishing a new birth sequence",
                        Undeclared(_declaredNodeMetrics, list));
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    carriedByBirth = true;
                }

                // A birth carries current values only; historical samples still go out as data.
                await PublishDataUnderLockAsync(null, carriedByBirth ? list.Where(m => m.IsHistorical).ToList() : list,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Declares the node's full metric set and, when connected, publishes a new birth sequence:
        /// an NBIRTH resets the session for the host, so every DBIRTH follows it.
        /// </summary>
        public async Task PublishNodeBirthAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken)
        {
            var list = ToValidatedList(_names.ResolveMetrics(null, metrics));

            await RunUnderLockAsync(async () =>
            {
                _metricStore.ReplaceNodeMetrics(list);
                if (SessionIsLive)
                {
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    BufferForBackfill(null, list);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Declares a device's full metric set and, when connected, publishes its DBIRTH.
        /// </summary>
        public async Task PublishDeviceBirthAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);
            if (_names.ResolveDeviceId(deviceId) is not { } device) return;
            var list = ToValidatedList(_names.ResolveMetrics(device, metrics));

            await RunUnderLockAsync(async () =>
            {
                _metricStore.ReplaceDeviceMetrics(device, list);
                if (SessionIsLive)
                {
                    await PublishDeviceBirthUnderLockAsync(device, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    BufferForBackfill(device, list);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public Task PublishDeviceMetricsAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);
            if (_names.ResolveDeviceId(deviceId) is not { } device) return Task.CompletedTask;
            return PublishDeviceDataAsync(device, ToValidatedList(_names.ResolveMetrics(device, metrics)), cancellationToken);
        }

        private async Task PublishDeviceDataAsync(string deviceId, List<Metric> list, CancellationToken cancellationToken)
        {
            if (list.Count == 0) return;

            await RunUnderLockAsync(async () =>
            {
                _metricStore.UpdateDeviceMetrics(deviceId, list);
                if (!SessionIsLive)
                {
                    BufferForBackfill(deviceId, list);
                    return;
                }

                var carriedByBirth = false;
                if (!_declaredDeviceMetrics.TryGetValue(deviceId, out var declared))
                {
                    _logger.Information("Device {DeviceId} has not been birthed on this connection; publishing its DBIRTH", deviceId);
                    await PublishDeviceBirthUnderLockAsync(deviceId, cancellationToken).ConfigureAwait(false);
                    carriedByBirth = true;
                }
                else if (!AllDeclared(declared, list))
                {
                    _logger.Information("Metric(s) {Names} not declared in the DBIRTH for {DeviceId}; publishing a new birth sequence",
                        Undeclared(declared, list), deviceId);
                    await PublishBirthSequenceUnderLockAsync(cancellationToken).ConfigureAwait(false);
                    carriedByBirth = true;
                }

                // A birth carries current values only; historical samples still go out as data.
                await PublishDataUnderLockAsync(deviceId, carriedByBirth ? list.Where(m => m.IsHistorical).ToList() : list,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks a device offline. It is forgotten, so later connections do not birth it again; publishing
        /// data or a DBIRTH for it brings it back. A second call for the same device does nothing.
        /// </summary>
        public async Task PublishDeviceDeathAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            ValidateDeviceId(deviceId);
            if (_names.ResolveDeviceId(deviceId) is not { } device) return;
            deviceId = device;

            await RunUnderLockAsync(async () =>
            {
                var wasBirthed = _declaredDeviceMetrics.Remove(deviceId);
                _metricStore.RemoveDevice(deviceId);

                // Offline, the connection's NDEATH has already told the host the device is gone.
                if (!wasBirthed || !SessionIsLive) return;

                var sequence = NextSeq();
                await PublishUnderLockAsync(SparkplugTopics.GetDeviceDeathTopic(_groupId, _edgeNodeId, deviceId),
                    SparkplugMessageFactory.CreateDeviceDeath(sequence), cancellationToken).ConfigureAwait(false);
                _logger.Information("Published DDEATH for device {DeviceId}, sequence {Sequence}", deviceId, sequence);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes only metrics that have changed since last update
        /// </summary>
        public async Task PublishChangedMetricsAsync(CancellationToken cancellationToken = default)
        {
            // Stored metrics already carry their published names, so they skip name resolution.
            var (nodeMetrics, deviceMetrics) = _metricStore.GetChangedMetrics();

            await PublishNodeDataAsync(ToValidatedList(nodeMetrics), cancellationToken).ConfigureAwait(false);

            foreach (var kvp in deviceMetrics)
            {
                await PublishDeviceDataAsync(kvp.Key, ToValidatedList(kvp.Value), cancellationToken).ConfigureAwait(false);
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
        /// Gets the current value of a node metric, by the name it was published with or under
        /// </summary>
        public Metric? GetNodeMetric(string metricName)
        {
            return _metricStore.GetNodeMetric(PublishedMetricName(metricName));
        }

        /// <summary>
        /// Gets the current value of a device metric, by the names it was published with or under
        /// </summary>
        public Metric? GetDeviceMetric(string deviceId, string metricName)
        {
            return _metricStore.GetDeviceMetric(PublishedDeviceId(deviceId), PublishedMetricName(metricName));
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
            return _metricStore.GetDeviceMetrics(PublishedDeviceId(deviceId));
        }

        private string PublishedDeviceId(string deviceId) =>
            _config.RenameInvalidNames ? SparkplugNames.MakeValidId(deviceId) : deviceId;

        private string PublishedMetricName(string metricName) =>
            _config.RenameInvalidNames ? SparkplugNames.MakeValidMetricName(metricName) : metricName;

        /// <summary>
        /// The group or edge node ID to publish under. Configuration validation has already rejected an
        /// invalid ID when renaming is off, so here it can only be renamed.
        /// </summary>
        private string ResolveConfiguredId(string setting, string value)
        {
            if (SparkplugNames.IsValidId(value)) return value;

            var valid = SparkplugNames.MakeValidId(value);
            _logger.Warning("{Setting} '{Value}' is not a valid Ignition name; publishing as '{Published}'", setting, value, valid);
            return valid;
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
            catch (Exception ex) when (IsConnectionLoss(ex))
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
            await PublishUnderLockAsync(SparkplugTopics.GetNodeBirthTopic(_groupId, _edgeNodeId),
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

            // History from the outage goes out after the births and before any live data.
            await PublishBackfillUnderLockAsync(cancellationToken).ConfigureAwait(false);

            _sessionBirthed = true;
        }

        private async Task PublishDeviceBirthUnderLockAsync(string deviceId, CancellationToken cancellationToken)
        {
            var metrics = _metricStore.GetDeviceMetrics(deviceId).ToList();
            var sequence = NextSeq();
            await PublishUnderLockAsync(SparkplugTopics.GetDeviceBirthTopic(_groupId, _edgeNodeId, deviceId),
                SparkplugMessageFactory.CreatePayload(metrics, sequence), cancellationToken).ConfigureAwait(false);

            _declaredDeviceMetrics[deviceId] = metrics.ToDictionary(m => m.Name, m => m.DataType);
            _logger.Information("Published DBIRTH for device {DeviceId} with {MetricCount} metrics, sequence {Sequence}",
                deviceId, metrics.Count, sequence);
        }

        /// <summary>
        /// NDATA (deviceId null) or DDATA carrying the given metrics. Only metrics declared in the current
        /// birth can be sent; that excludes only a historical sample whose data type differs from the
        /// stored metric's, which is dropped with a warning. If the connection drops, the samples go to the
        /// backfill buffer (when enabled) before the error propagates.
        /// </summary>
        private async Task PublishDataUnderLockAsync(string? deviceId, List<Metric> metrics, CancellationToken cancellationToken)
        {
            var declared = deviceId is null ? _declaredNodeMetrics : _declaredDeviceMetrics[deviceId];
            var sendable = metrics.Where(m => IsDeclared(declared, m.Name, m.DataType)).ToList();
            if (sendable.Count < metrics.Count)
            {
                _logger.Warning("Dropped {Count} historical sample(s) for {Target}: their data type differs from the declared metric",
                    metrics.Count - sendable.Count, deviceId ?? "the node");
            }
            if (sendable.Count == 0) return;

            var sequence = NextSeq();
            try
            {
                await PublishUnderLockAsync(DataTopic(deviceId), SparkplugMessageFactory.CreatePayload(sendable, sequence),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConnectionLoss(ex))
            {
                BufferForBackfill(deviceId, sendable);
                throw;
            }

            _logger.Debug("Published {Kind} for {Target} with {MetricCount} metrics, sequence {Sequence}",
                deviceId is null ? "NDATA" : "DDATA", deviceId ?? "the node", sendable.Count, sequence);
        }

        /// <summary>
        /// Keeps samples that could not be published live, when backfill is enabled. A full buffer drops
        /// its oldest sample; that is logged once per outage.
        /// </summary>
        private void BufferForBackfill(string? deviceId, IEnumerable<Metric> metrics)
        {
            if (_backfill is null) return;

            foreach (var metric in metrics)
            {
                if (_backfill.Add(deviceId, metric) && !_backfillOverflowLogged)
                {
                    _backfillOverflowLogged = true;
                    _logger.Warning("Backfill buffer is full ({Capacity} samples): dropping the oldest samples until the connection is restored",
                        _backfill.Capacity);
                }
            }
        }

        /// <summary>
        /// Sends the buffered samples as history (is_historical, original timestamps), oldest first, in
        /// messages of at most <see cref="BackfillMaxMetricsPerMessage"/> metrics. Runs at the end of a
        /// birth sequence, so only metrics the births declared can be sent. A sample that is the value the
        /// birth already carried as current is skipped. If the connection drops part way, the unsent
        /// samples are kept for the next connection.
        /// </summary>
        private async Task PublishBackfillUnderLockAsync(CancellationToken cancellationToken)
        {
            if (_backfill is not { Count: > 0 }) return;

            var samples = _backfill.TakeAll();
            var done = new bool[samples.Count];
            int published = 0, undeclared = 0, messages = 0;

            try
            {
                foreach (var target in Enumerable.Range(0, samples.Count).GroupBy(i => samples[i].DeviceId))
                {
                    var deviceId = target.Key;
                    var declared = deviceId is null ? _declaredNodeMetrics : _declaredDeviceMetrics.GetValueOrDefault(deviceId);

                    var toSend = new List<int>();
                    foreach (var i in target)
                    {
                        var sample = samples[i];
                        if (declared is null || !IsDeclared(declared, sample.Name, sample.DataType))
                        {
                            undeclared++; // device retired, or metric or type changed during the outage
                            done[i] = true;
                        }
                        else if (IsCurrentValue(deviceId, sample))
                        {
                            done[i] = true; // already in the birth, as current state
                        }
                        else
                        {
                            toSend.Add(i);
                        }
                    }

                    foreach (var chunk in toSend.Chunk(BackfillMaxMetricsPerMessage))
                    {
                        var metrics = chunk.Select(i => samples[i].ToHistoricalMetric()).ToList();
                        await PublishUnderLockAsync(DataTopic(deviceId), SparkplugMessageFactory.CreatePayload(metrics, NextSeq()),
                            cancellationToken).ConfigureAwait(false);

                        foreach (var i in chunk) done[i] = true;
                        published += chunk.Length;
                        messages++;
                    }
                }
            }
            catch
            {
                _backfill.Restore(Enumerable.Range(0, samples.Count).Where(i => !done[i]).Select(i => samples[i]));
                throw;
            }

            _logger.Information(
                "Backfilled {Published} samples in {Messages} messages ({Dropped} dropped while the buffer was full, {Undeclared} no longer declared)",
                published, messages, _backfill.Dropped, undeclared);
            _backfill.ResetDropped();
            _backfillOverflowLogged = false;
        }

        private bool IsCurrentValue(string? deviceId, BufferedSample sample)
        {
            var current = deviceId is null
                ? _metricStore.GetNodeMetric(sample.Name)
                : _metricStore.GetDeviceMetric(deviceId, sample.Name);

            return current != null && current.Timestamp == sample.Timestamp && Equals(current.Value, sample.Value);
        }

        private string DataTopic(string? deviceId) => deviceId is null
            ? SparkplugTopics.GetNodeDataTopic(_groupId, _edgeNodeId)
            : SparkplugTopics.GetDeviceDataTopic(_groupId, _edgeNodeId, deviceId);

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
                    .WithTopic(SparkplugTopics.GetNodeDeathTopic(_groupId, _edgeNodeId))
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
            if (e.ApplicationMessage.Topic != SparkplugTopics.GetNodeCommandTopic(_groupId, _edgeNodeId))
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

        private static bool IsDeclared(Dictionary<string, MetricDataType> declared, string name, MetricDataType type) =>
            declared.TryGetValue(name, out var declaredType) && declaredType == type;

        private static bool AllDeclared(Dictionary<string, MetricDataType> declared, List<Metric> metrics) =>
            metrics.All(m => IsDeclared(declared, m.Name, m.DataType));

        private static bool IsConnectionLoss(Exception ex) => ex is MqttCommunicationException or ObjectDisposedException;

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
