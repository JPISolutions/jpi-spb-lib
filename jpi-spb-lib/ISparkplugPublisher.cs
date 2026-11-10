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
        /// Starts connecting to the MQTT broker; returns once the first attempt has finished. The publisher
        /// keeps the connection up from then on (reconnecting with back-off) and publishes NBIRTH and every
        /// DBIRTH on each new connection. An unreachable broker does not make this throw.
        /// </summary>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes NDEATH and disconnects from the MQTT broker
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