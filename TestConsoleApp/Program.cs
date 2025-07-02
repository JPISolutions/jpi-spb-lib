using System;
using Serilog;
using SparkplugB.Publisher;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .MinimumLevel.Debug()
    .CreateLogger();
try
{
    // Configure publisher
    var config = new SparkplugConfiguration
    {
        GroupId = "TestGroup",
        EdgeNodeId = "TestNode",
        MqttBroker = "olds-mqtt01", // Free test broker
        MqttPort = 1883
    };

    Log.Information("Starting Sparkplug B Publisher Test");
    Log.Information("Configuration: Group={Group}, Node={Node}, Broker={Broker}:{Port}",
        config.GroupId, config.EdgeNodeId, config.MqttBroker, config.MqttPort);

    // Create and connect
    using var publisher = new SparkplugPublisher(config);
    publisher.SetLogger(Log.Logger);

    // Connect
    Log.Information("Connecting to MQTT broker...");
    await publisher.ConnectAsync();
    Log.Information("Connected successfully!");

    // Wait a moment for NBIRTH to be published
    await Task.Delay(1000);

    // Publish some node metrics
    Log.Information("Publishing node metrics...");
    var nodeMetrics = new List<Metric>
            {
                new Metric("Temperature", 23.5, MetricDataType.Double),
                new Metric("Status", "Online", MetricDataType.String),
                new Metric("Uptime", 12345, MetricDataType.Int64)
            };
    await publisher.PublishNodeMetricsAsync(nodeMetrics);

    // Publish device birth
    var deviceId = "Device1";
    Log.Information("Publishing device birth for {DeviceId}...", deviceId);
    var deviceBirthMetrics = new List<Metric>
            {
                new Metric("Voltage", 230.5f, MetricDataType.Float),
                new Metric("Current", 5.2f, MetricDataType.Float),
                new Metric("Power", true, MetricDataType.Boolean),
                new Metric("SerialNumber", "SN123456", MetricDataType.String)
            };
    await publisher.PublishDeviceBirthAsync(deviceId, deviceBirthMetrics);

    // Publish some device data updates
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(2000);

        Log.Information("Publishing device data update {Index}...", i + 1);
        var deviceDataMetrics = new List<Metric>
                {
                    new Metric("Voltage", 230.5f + (float)(Random.Shared.NextDouble() * 2 - 1), MetricDataType.Float),
                    new Metric("Current", 5.2f + (float)(Random.Shared.NextDouble() * 0.5 - 0.25), MetricDataType.Float),
                    new Metric("Power", i % 2 == 0, MetricDataType.Boolean)
                };
        await publisher.PublishDeviceMetricsAsync(deviceId, deviceDataMetrics);
    }

    // Publish device death
    Log.Information("Publishing device death for {DeviceId}...", deviceId);
    await publisher.PublishDeviceDeathAsync(deviceId);

    // Wait before disconnecting
    await Task.Delay(2000);

    // Disconnect
    Log.Information("Disconnecting...");
    await publisher.DisconnectAsync();
    Log.Information("Disconnected successfully!");
}
catch (Exception ex)
{
    Log.Error(ex, "Error in test program");
}
finally
{
    Log.CloseAndFlush();
}
