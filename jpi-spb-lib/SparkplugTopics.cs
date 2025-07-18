namespace SparkplugB.Publisher
{
    /// <summary>
    /// Helper class for building Sparkplug-B topic names
    /// </summary>
    internal static class SparkplugTopics
    {
        private const string SpBv1_0 = "spBv1.0";

        /// <summary>
        /// Builds a Node Birth topic: spBv1.0/{group_id}/NBIRTH/{edge_node_id}
        /// </summary>
        public static string GetNodeBirthTopic(string groupId, string edgeNodeId)
        {
            return $"{SpBv1_0}/{groupId}/NBIRTH/{edgeNodeId}";
        }

        /// <summary>
        /// Builds a Node Data topic: spBv1.0/{group_id}/NDATA/{edge_node_id}
        /// </summary>
        public static string GetNodeDataTopic(string groupId, string edgeNodeId)
        {
            return $"{SpBv1_0}/{groupId}/NDATA/{edgeNodeId}";
        }

        /// <summary>
        /// Builds a Node Death topic: spBv1.0/{group_id}/NDEATH/{edge_node_id}
        /// </summary>
        public static string GetNodeDeathTopic(string groupId, string edgeNodeId)
        {
            return $"{SpBv1_0}/{groupId}/NDEATH/{edgeNodeId}";
        }

        /// <summary>
        /// Builds a Device Birth topic: spBv1.0/{group_id}/DBIRTH/{edge_node_id}/{device_id}
        /// </summary>
        public static string GetDeviceBirthTopic(string groupId, string edgeNodeId, string deviceId)
        {
            return $"{SpBv1_0}/{groupId}/DBIRTH/{edgeNodeId}/{deviceId}";
        }

        /// <summary>
        /// Builds a Device Data topic: spBv1.0/{group_id}/DDATA/{edge_node_id}/{device_id}
        /// </summary>
        public static string GetDeviceDataTopic(string groupId, string edgeNodeId, string deviceId)
        {
            return $"{SpBv1_0}/{groupId}/DDATA/{edgeNodeId}/{deviceId}";
        }

        /// <summary>
        /// Builds a Device Death topic: spBv1.0/{group_id}/DDEATH/{edge_node_id}/{device_id}
        /// </summary>
        public static string GetDeviceDeathTopic(string groupId, string edgeNodeId, string deviceId)
        {
            return $"{SpBv1_0}/{groupId}/DDEATH/{edgeNodeId}/{deviceId}";
        }

        /// <summary>
        /// Builds a Node Command topic: spBv1.0/{group_id}/NCMD/{edge_node_id}
        /// </summary>
        public static string GetNodeCommandTopic(string groupId, string edgeNodeId)
        {
            return $"{SpBv1_0}/{groupId}/NCMD/{edgeNodeId}";
        }

        /// <summary>
        /// Builds a Device Command topic: spBv1.0/{group_id}/DCMD/{edge_node_id}/{device_id}
        /// </summary>
        public static string GetDeviceCommandTopic(string groupId, string edgeNodeId, string deviceId)
        {
            return $"{SpBv1_0}/{groupId}/DCMD/{edgeNodeId}/{deviceId}";
        }

        /// <summary>
        /// Builds a State topic: spBv1.0/STATE/{scada_host_id}
        /// </summary>
        public static string GetStateTopic(string scadaHostId)
        {
            return $"{SpBv1_0}/STATE/{scadaHostId}";
        }
    }
}