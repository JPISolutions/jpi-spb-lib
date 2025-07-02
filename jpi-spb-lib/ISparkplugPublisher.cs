using Serilog;

namespace SparkplugB.Publisher
{
    /// <summary>
    /// Interface for publishing Sparkplug-B messages
    /// </summary>
    public interface ISparkplugPublisher : IDisposable
    {
        /// <summary>
        /// Gets whether the publisher is connected to the MQTT broker
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Connects to the MQTT broker and publishes NBIRTH
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnects from the MQTT broker
        /// </summary>
        Task DisconnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes node metrics (NDATA)
        /// </summary>
        Task PublishNodeMetricsAsync(IEnumerable<Metric> metrics, CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes device birth (DBIRTH)
        /// </summary>
        Task PublishDeviceBirthAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes device metrics (DDATA)
        /// </summary>
        Task PublishDeviceMetricsAsync(string deviceId, IEnumerable<Metric> metrics, CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes device death (DDEATH)
        /// </summary>
        Task PublishDeviceDeathAsync(string deviceId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the logger instance
        /// </summary>
        void SetLogger(ILogger logger);
    }
}