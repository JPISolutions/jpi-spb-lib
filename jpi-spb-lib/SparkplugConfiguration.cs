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
        /// File that records the bdSeq of the last MQTT connection, so the sequence continues across
        /// restarts. A relative path is resolved against the application folder. Null or empty uses one
        /// file per edge node, "&lt;GroupId&gt;.&lt;EdgeNodeId&gt;.bdseq", in the operating system's state folder:
        /// %ProgramData%\JPI Solutions\Sparkplug on Windows; elsewhere $STATE_DIRECTORY (set by systemd's
        /// StateDirectory=), else $XDG_STATE_HOME/jpi-sparkplug, else ~/.local/state/jpi-sparkplug.
        /// </summary>
        public string? BdSeqFilePath { get; set; }

        /// <summary>
        /// Group, edge node and device IDs and metric names must follow Ignition's naming rules (see
        /// <see cref="SparkplugNames"/>). When true (the default), an invalid name is published under its
        /// valid form, with '_' for each character not allowed, and a warning is logged once. When false,
        /// an invalid metric or device is not published (an error is logged once), and an invalid
        /// GroupId or EdgeNodeId is a configuration error.
        /// </summary>
        public bool RenameInvalidNames { get; set; } = true;

        /// <summary>
        /// Delay before retrying after a failed connection attempt. It doubles on each consecutive
        /// failure, up to <see cref="ReconnectMaxDelaySeconds"/>.
        /// </summary>
        public double ReconnectMinDelaySeconds { get; set; } = 1;

        /// <summary>
        /// Upper bound for the reconnect back-off.
        /// </summary>
        public double ReconnectMaxDelaySeconds { get; set; } = 60;

        /// <summary>
        /// How long a single connection attempt may take before it is abandoned and retried.
        /// </summary>
        public double ConnectTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Keep the samples published while there is no connection and send them as history
        /// (is_historical, with their original timestamps) right after the next birth sequence. The births
        /// carry each metric's latest value as current state. Off by default. The buffer is in memory, so
        /// a restart during an outage loses it.
        /// </summary>
        public bool BackfillEnabled { get; set; } = false;

        /// <summary>
        /// The most samples kept for backfill (one sample is one metric value). When the buffer is full the
        /// oldest samples are dropped, and a warning is logged once per outage.
        /// </summary>
        public int BackfillMaxSamples { get; set; } = 10000;

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

            if (!RenameInvalidNames && !SparkplugNames.IsValidId(GroupId))
            {
                errorMessage = $"GroupId '{GroupId}' is not a valid Ignition name (and RenameInvalidNames is off)";
                return false;
            }

            if (!RenameInvalidNames && !SparkplugNames.IsValidId(EdgeNodeId))
            {
                errorMessage = $"EdgeNodeId '{EdgeNodeId}' is not a valid Ignition name (and RenameInvalidNames is off)";
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

            if (ReconnectMinDelaySeconds <= 0 || ReconnectMaxDelaySeconds < ReconnectMinDelaySeconds)
            {
                errorMessage = "ReconnectMinDelaySeconds must be positive and no greater than ReconnectMaxDelaySeconds";
                return false;
            }

            if (ConnectTimeoutSeconds <= 0)
            {
                errorMessage = "ConnectTimeoutSeconds must be positive";
                return false;
            }

            if (BackfillEnabled && BackfillMaxSamples <= 0)
            {
                errorMessage = "BackfillMaxSamples must be positive when BackfillEnabled is set";
                return false;
            }

            return true;
        }
    }
}