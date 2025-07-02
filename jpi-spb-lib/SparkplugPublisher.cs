using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Serilog;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Implementation of Sparkplug-B publisher
    /// </summary>
    public class SparkplugPublisher : ISparkplugPublisher
    {
        private readonly SparkplugConfiguration _config;
        private readonly IManagedMqttClient _mqttClient;
        private ILogger _logger;
        private bool _disposed;
        private ulong _sequenceNumber;
        private readonly ulong _birthSequence;

        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public SparkplugPublisher(SparkplugConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid configuration: {error}", nameof(config));
            }

            _mqttClient = new MqttFactory().CreateManagedMqttClient();
            _logger = Log.Logger; // Default to global logger
            _birthSequence = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sequenceNumber = 0;
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
                var willPayload = SparkplugMessageFactory.CreateNodeDeath(_birthSequence);
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
                    // Publish NBIRTH
                    await PublishNodeBirthAsync(cancellationToken);
                };

                _mqttClient.DisconnectedAsync += async e =>
                {
                    _logger.Warning("Disconnected from MQTT broker: {Reason}", e.Reason);
                    await Task.CompletedTask;
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

        private async Task PublishNodeBirthAsync(CancellationToken cancellationToken)
        {
            try
            {
                var topic = SparkplugTopics.GetNodeBirthTopic(_config.GroupId, _config.EdgeNodeId);

                // Create initial node metrics if needed
                var nodeMetrics = new List<Metric>();
                // Add any additional node-level metrics here if needed

                // Use the factory method for NBIRTH which includes bdSeq and Node Control/Rebirth
                var payload = SparkplugMessageFactory.CreateNodeBirth(nodeMetrics, _birthSequence, 0);

                await PublishAsync(topic, payload, cancellationToken);

                _logger.Information("Published NBIRTH with bdSeq {BirthSequence}", _birthSequence);
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