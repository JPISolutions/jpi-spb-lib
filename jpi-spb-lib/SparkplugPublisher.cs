using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Serilog;
using Com.Cirruslink.Sparkplug.Protobuf;
using MQTTnet.Packets;


namespace SparkplugB.Publisher
{
    /// <summary>
    /// Implementation of Sparkplug-B publisher
    /// </summary>
    public class SparkplugPublisher : ISparkplugPublisher
    {
        private readonly SparkplugConfiguration _config;
        private readonly IManagedMqttClient _mqttClient;
        private readonly MetricStore _metricStore;
        private ILogger _logger;
        private bool _disposed;
        private ulong _sequenceNumber;
        private readonly BdSeqManager _bdSeqManager = new();
        private ulong _bdSeq = 0;


        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public SparkplugPublisher(SparkplugConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid configuration: {error}", nameof(config));
            }

            _mqttClient = new MqttFactory().CreateManagedMqttClient();
            _metricStore = new MetricStore();
            _logger = Log.Logger; // Default to global logger
            _sequenceNumber = 0;
            _bdSeq = _bdSeqManager.Load();
            _logger.Information("Loaded bdSeq from file: {BdSeq}", _bdSeq);
        }

        public void SetLogger(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.Information("Connecting to MQTT broker {Broker}:{Port}", _config.MqttBroker, _config.MqttPort);

                var clientId = $"{_config.GroupId}.{_config.EdgeNodeId}";

                // Create the client options
                var clientOptions = new MqttClientOptionsBuilder()
                    .WithClientId(clientId)
                    .WithTcpServer(_config.MqttBroker, _config.MqttPort)
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
                    .WithCleanSession(true);

                // Add credentials if provided
                if (!string.IsNullOrEmpty(_config.Username))
                {
                    clientOptions.WithCredentials(_config.Username, _config.Password);
                }

                // Configure TLS if enabled
                if (_config.UseTls)
                {
                    clientOptions.WithTlsOptions(o => o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12));
                }

                // Set up the will message (NDEATH)
                var willTopic = SparkplugTopics.GetNodeDeathTopic(_config.GroupId, _config.EdgeNodeId);
                var willPayload = SparkplugMessageFactory.CreateNodeDeath(_bdSeq);
                clientOptions.WithWillTopic(willTopic)
                           .WithWillPayload(willPayload)
                           .WithWillRetain(false)
                           .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

                // Create managed client options
                var managedOptions = new ManagedMqttClientOptionsBuilder()
                    .WithAutoReconnectDelay(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
                    .WithClientOptions(clientOptions.Build())
                    .Build();

                // Subscribe to connection events
                _mqttClient.ConnectedAsync += async e =>
                {
                    _logger.Information("Successfully connected to MQTT broker");
                    // Reset sequence number on reconnect
                    _sequenceNumber = 0;

                    var ncmdTopic = SparkplugTopics.GetNodeCommandTopic(_config.GroupId, _config.EdgeNodeId);
                    await _mqttClient.SubscribeAsync(new List<MqttTopicFilter>
                    {
                        new MqttTopicFilterBuilder()
                            .WithTopic(ncmdTopic)
                            .WithAtLeastOnceQoS()
                            .Build()
                    });

                    _logger.Information("Subscribed to NCMD topic: {Topic}", ncmdTopic);

                    // Publish NBIRTH with all current node metrics, if any
                    // On first run there might not be any metrics yet. 
                    var nodeMetrics = _metricStore.GetNodeMetrics().ToList();
                    if (nodeMetrics.Any())
                    {
                        await PublishNodeBirthAsync(nodeMetrics, cancellationToken);
                    }
                };

                _mqttClient.DisconnectedAsync += async e =>
                {
                    _logger.Warning("Disconnected from MQTT broker: {Reason}", e.Reason);
                    await Task.CompletedTask;
                };

                _mqttClient.ApplicationMessageReceivedAsync += async e =>
                {
                    var topic = e.ApplicationMessage.Topic;
                    if (topic == SparkplugTopics.GetNodeCommandTopic(_config.GroupId, _config.EdgeNodeId))
                    {
                        _logger.Information("Received NCMD message");
                        try
                        {
                            var payload = Payload.Parser.ParseFrom(e.ApplicationMessage.PayloadSegment);
                            var rebirthRequested = payload.Metrics.Any(m =>
                                m.Name == "Node Control/Rebirth" &&
                                m.Datatype == (uint)MetricDataType.Boolean &&
                                m.BooleanValue == true);

                            if (rebirthRequested)
                            {
                                _logger.Information("Rebirth requested via NCMD");
                                await HandleRebirthAsync(); // Calls your existing rebirth logic
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Failed to handle NCMD payload");
                        }
                    }
                };

                // Start the managed client
                await _mqttClient.StartAsync(managedOptions);

                // Wait a bit to ensure connection is established
                var timeout = TimeSpan.FromSeconds(10);
                var startTime = DateTime.UtcNow;

                while (!_mqttClient.IsConnected && DateTime.UtcNow - startTime < timeout)
                {
                    await Task.Delay(100, cancellationToken);
                }

                if (!_mqttClient.IsConnected)
                {
                    throw new TimeoutException("Failed to connect to MQTT broker within timeout period");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to MQTT broker");
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_mqttClient.IsConnected)
                {
                    _logger.Information("Disconnecting from MQTT broker");

                    // The NDEATH will be sent automatically due to the will message
                    await _mqttClient.StopAsync();

                    _logger.Information("Disconnected from MQTT broker");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error disconnecting from MQTT broker");
                throw;
            }
        }

        public async Task PublishNodeMetricsAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            try
            {
                // Update the metric store
                _metricStore.UpdateNodeMetrics(metrics);

                var topic = SparkplugTopics.GetNodeDataTopic(_config.GroupId, _config.EdgeNodeId);
                var sequence = IncrementSequence();
                var payload = SparkplugMessageFactory.CreatePayload(metrics, sequence);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Debug("Published NDATA with {MetricCount} metrics, sequence {Sequence}",
                    metrics.Count(), sequence);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing node metrics");
                throw;
            }
        }

        public async Task PublishDeviceBirthAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            ValidateDeviceId(deviceId);

            try
            {
                // Store all device metrics
                _metricStore.UpdateDeviceMetrics(deviceId, metrics);

                var topic = SparkplugTopics.GetDeviceBirthTopic(_config.GroupId, _config.EdgeNodeId, deviceId);
                var sequence = IncrementSequence();
                var payload = SparkplugMessageFactory.CreatePayload(metrics, sequence);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Information("Published DBIRTH for device {DeviceId} with {MetricCount} metrics, sequence {Sequence}",
                    deviceId, metrics.Count(), sequence);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing device birth for {DeviceId}", deviceId);
                throw;
            }
        }

        public async Task PublishDeviceMetricsAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            ValidateDeviceId(deviceId);

            try
            {
                // Update the metric store
                _metricStore.UpdateDeviceMetrics(deviceId, metrics);

                var topic = SparkplugTopics.GetDeviceDataTopic(_config.GroupId, _config.EdgeNodeId, deviceId);
                var sequence = IncrementSequence();
                var payload = SparkplugMessageFactory.CreatePayload(metrics, sequence);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Debug("Published DDATA for device {DeviceId} with {MetricCount} metrics, sequence {Sequence}",
                    deviceId, metrics.Count(), sequence);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing device metrics for {DeviceId}", deviceId);
                throw;
            }
        }

        public async Task PublishDeviceDeathAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            ValidateDeviceId(deviceId);

            try
            {
                // Remove device from metric store
                _metricStore.RemoveDevice(deviceId);

                var topic = SparkplugTopics.GetDeviceDeathTopic(_config.GroupId, _config.EdgeNodeId, deviceId);
                var sequence = IncrementSequence();
                var payload = SparkplugMessageFactory.CreateDeviceDeath(sequence);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Information("Published DDEATH for device {DeviceId}, sequence {Sequence}",
                    deviceId, sequence);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing device death for {DeviceId}", deviceId);
                throw;
            }
        }

        /// <summary>
        /// Publishes only metrics that have changed since last update
        /// </summary>
        public async Task PublishChangedMetricsAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            try
            {
                var (nodeMetrics, deviceMetrics) = _metricStore.GetChangedMetrics();

                // Publish changed node metrics
                if (nodeMetrics.Any())
                {
                    await PublishNodeMetricsAsync(nodeMetrics, cancellationToken);
                }

                // Publish changed device metrics
                foreach (var kvp in deviceMetrics)
                {
                    if (kvp.Value.Any())
                    {
                        await PublishDeviceMetricsAsync(kvp.Key, kvp.Value, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing changed metrics");
                throw;
            }
        }

        /// <summary>
        /// Handles a rebirth request by republishing all births
        /// </summary>
        public async Task HandleRebirthAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            try
            {
                _logger.Information("Handling rebirth request");

                // Reset sequence number
                _sequenceNumber = 0;

                // Reset change tracking to ensure all metrics are sent
                _metricStore.ResetChangeTracking();

                // Publish NBIRTH with all current node metrics
                var nodeMetrics = _metricStore.GetNodeMetrics().ToList();
                if (nodeMetrics.Any())
                {
                    await PublishNodeBirthAsync(nodeMetrics, cancellationToken);
                }

                // Publish DBIRTH for all known devices
                foreach (var deviceId in _metricStore.GetDeviceIds())
                {
                    var metrics = _metricStore.GetDeviceMetrics(deviceId);
                    if (metrics.Any())
                    {
                        var topic = SparkplugTopics.GetDeviceBirthTopic(_config.GroupId, _config.EdgeNodeId, deviceId);
                        var sequence = IncrementSequence();
                        var payload = SparkplugMessageFactory.CreatePayload(metrics, sequence);

                        await PublishAsync(topic, payload, cancellationToken);

                        _logger.Information("Re-published DBIRTH for device {DeviceId} with {MetricCount} metrics",
                            deviceId, metrics.Count());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error handling rebirth");
                throw;
            }
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

        public async Task PublishNodeBirthAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken)
        {

            

            try
            {
                // Update the metric store
                // Is this redundant and causing a bug? We call GetMetrics and pass the metrics in prior
                // to calling this... 
                _metricStore.UpdateNodeMetrics(metrics);

                var topic = SparkplugTopics.GetNodeBirthTopic(_config.GroupId, _config.EdgeNodeId);

                // Use the factory method for NBIRTH which includes bdSeq and Node Control/Rebirth
                _bdSeq = _bdSeqManager.IncrementAndSave(_bdSeq); // Increment bdSeq for each NBIRTH

                var payload = SparkplugMessageFactory.CreateNodeBirth(metrics, _bdSeq, 0);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Information("Published NBIRTH with bdSeq {BdSeq} and {MetricCount} metrics",
                    _bdSeq, metrics.Count());

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error publishing NBIRTH");
                throw;
            }
        }

        private async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.EnqueueAsync(message);

            _logger.Debug("Published message to topic {Topic}, payload size: {Size} bytes",
                topic, payload.Length);
        }

        private ulong IncrementSequence()
        {
            // Sparkplug B sequence numbers wrap at 256
            _sequenceNumber = (_sequenceNumber + 1) % 256;
            return _sequenceNumber;
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected to MQTT broker");
            }
        }

        private void ValidateDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                if (_mqttClient?.IsConnected == true)
                {
                    _mqttClient.StopAsync().GetAwaiter().GetResult();
                }

                _mqttClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error disposing SparkplugPublisher");
            }

            _disposed = true;
        }
    }
}