namespace SparkplugB.Publisher
{
    /// <summary>
    /// Configuration settings for Sparkplug-B publisher
    /// </summary>
    public class SparkplugConfiguration
    {
        /// <summary>
        /// The group ID for this Sparkplug edge node
        /// </summary>
        public string GroupId { get; set; } = "DefaultGroup";

        /// <summary>
        /// The edge node ID 
        /// </summary>
        public string EdgeNodeId { get; set; } = "DefaultNode";

        /// <summary>
        /// MQTT broker address (e.g., "broker.hivemq.com" or "192.168.1.100")
        /// </summary>
        public string MqttBroker { get; set; } = "localhost";

        /// <summary>
        /// MQTT broker port (typically 1883 for non-TLS, 8883 for TLS)
        /// </summary>
        public int MqttPort { get; set; } = 1883;

        /// <summary>
        /// Optional username for MQTT authentication
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// Optional password for MQTT authentication
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// Use TLS/SSL for MQTT connection
        /// </summary>
        public bool UseTls { get; set; } = false;

        /// <summary>
        /// MQTT keep alive interval in seconds
        /// </summary>
        public int KeepAliveSeconds { get; set; } = 60;

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public bool Validate(out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(GroupId))
            {
                errorMessage = "GroupId is required";
                return false;
            }

            if (string.IsNullOrWhiteSpace(EdgeNodeId))
            {
                errorMessage = "EdgeNodeId is required";
                return false;
            }

            if (string.IsNullOrWhiteSpace(MqttBroker))
            {
                errorMessage = "MqttBroker is required";
                return false;
            }

            if (MqttPort <= 0 || MqttPort > 65535)
            {
                errorMessage = "MqttPort must be between 1 and 65535";
                return false;
            }

            return true;
        }
    }
}