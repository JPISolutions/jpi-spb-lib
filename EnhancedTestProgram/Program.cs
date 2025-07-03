using Serilog;
using SparkplugB.Publisher;

class EnhancedTestProgram
{
    static async Task Main(string[] args)
    {
        // Configure Serilog for detailed logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            // Create configuration
            var config = new SparkplugConfiguration
            {
                GroupId = "TestGroup",
                EdgeNodeId = "CachedNode",
                MqttBroker = "olds-mqtt01",
                MqttPort = 1883,
                UseTls = false,
                KeepAliveSeconds = 30
            };

            Log.Information("Starting Enhanced Sparkplug B Publisher Test with Metric Caching");
            Log.Information("Configuration: Group={Group}, Node={Node}, Broker={Broker}:{Port}",
                config.GroupId, config.EdgeNodeId, config.MqttBroker, config.MqttPort);

            // Create publisher
            using var publisher = new SparkplugPublisher(config);
            publisher.SetLogger(Log.Logger);

            // Connect
            Log.Information("Connecting to MQTT broker...");
            await publisher.ConnectAsync();
            Log.Information("Connected successfully!");

            // Wait for NBIRTH
            await Task.Delay(1000);

            // Scenario 1: Initial node metrics
            Log.Information("\n=== Scenario 1: Publishing initial node metrics ===");
            var nodeMetrics = new List<Metric>
            {
                new Metric("Temperature", 22.5, MetricDataType.Double),
                new Metric("Humidity", 45.0, MetricDataType.Double),
                new Metric("Status", "Online", MetricDataType.String),
                new Metric("RunTime", 0, MetricDataType.Int64),
                new Metric("ErrorCount", 0, MetricDataType.Int32)
            };
            await publisher.PublishNodeMetricsAsync(nodeMetrics);
            Log.Information("Published {Count} node metrics", nodeMetrics.Count);

            // Scenario 2: Add devices with metrics
            Log.Information("\n=== Scenario 2: Adding devices with birth certificates ===");

            // Device 1
            var device1Metrics = new List<Metric>
            {
                new Metric("Voltage", 230.0f, MetricDataType.Float),
                new Metric("Current", 5.0f, MetricDataType.Float),
                new Metric("Power", 1150.0f, MetricDataType.Float),
                new Metric("Frequency", 50.0f, MetricDataType.Float),
                new Metric("PowerFactor", 0.95f, MetricDataType.Float),
                new Metric("Status", true, MetricDataType.Boolean)
            };
            await publisher.PublishDeviceBirthAsync("Motor1", device1Metrics);

            // Device 2
            var device2Metrics = new List<Metric>
            {
                new Metric("Temperature", 65.0, MetricDataType.Double),
                new Metric("Pressure", 101.3, MetricDataType.Double),
                new Metric("Flow", 45.5, MetricDataType.Double),
                new Metric("ValvePosition", 75, MetricDataType.Int32),
                new Metric("AlarmActive", false, MetricDataType.Boolean)
            };
            await publisher.PublishDeviceBirthAsync("Sensor1", device2Metrics);

            Log.Information("Added 2 devices with metrics");

            // Scenario 3: Update only changed metrics
            Log.Information("\n=== Scenario 3: Testing change detection ===");
            await Task.Delay(2000);

            // Update some node metrics (only Temperature and RunTime change)
            Log.Information("Updating only Temperature and RunTime...");
            var updatedNodeMetrics = new List<Metric>
            {
                new Metric("Temperature", 23.1, MetricDataType.Double),  // Changed
                new Metric("Humidity", 45.0, MetricDataType.Double),     // Same
                new Metric("RunTime", 2, MetricDataType.Int64)          // Changed
            };
            await publisher.PublishNodeMetricsAsync(updatedNodeMetrics);

            // Use PublishChangedMetricsAsync to send only what changed
            Log.Information("\n=== Scenario 4: Using PublishChangedMetricsAsync ===");
            await Task.Delay(2000);

            // Update metrics in the store but use change detection
            var motor1Updates = new List<Metric>
            {
                new Metric("Current", 5.5f, MetricDataType.Float),      // Changed
                new Metric("Power", 1265.0f, MetricDataType.Float),     // Changed
                new Metric("Voltage", 230.0f, MetricDataType.Float),    // Same
                new Metric("Status", true, MetricDataType.Boolean)      // Same
            };

            // First update the metrics
            await publisher.PublishDeviceMetricsAsync("Motor1", motor1Updates);

            Log.Information("Changed metrics have been published");

            // Scenario 5: Query current metric values
            Log.Information("\n=== Scenario 5: Querying current metric values ===");

            var currentTemp = publisher.GetNodeMetric("Temperature");
            if (currentTemp != null)
            {
                Log.Information("Current Temperature: {Value} (Last updated: {Timestamp})",
                    currentTemp.Value, DateTimeOffset.FromUnixTimeMilliseconds((long)currentTemp.Timestamp));
            }

            var motor1Current = publisher.GetDeviceMetric("Motor1", "Current");
            if (motor1Current != null)
            {
                Log.Information("Motor1 Current: {Value} A", motor1Current.Value);
            }

            // Show all metrics for a device
            Log.Information("\nAll metrics for Motor1:");
            foreach (var metric in publisher.GetAllDeviceMetrics("Motor1"))
            {
                Log.Information("  {Name}: {Value} ({Type})", metric.Name, metric.Value, metric.DataType);
            }

            // Scenario 6: Simulate metric updates over time
            Log.Information("\n=== Scenario 6: Simulating real-time updates ===");
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(3000);

                // Simulate changing values
                var runtime = 3 + (i * 3);
                var temp = 22.5 + (Random.Shared.NextDouble() * 2);
                var current = 5.0f + (float)(Random.Shared.NextDouble() * 2 - 1);
                var power = current * 230.0f;

                // Update node metrics
                var nodeUpdates = new List<Metric>
                {
                    new Metric("Temperature", temp, MetricDataType.Double),
                    new Metric("RunTime", runtime, MetricDataType.Int64),
                    new Metric("ErrorCount", i % 3 == 0 ? 1 : 0, MetricDataType.Int32)
                };

                // Update device metrics
                var motorUpdates = new List<Metric>
                {
                    new Metric("Current", current, MetricDataType.Float),
                    new Metric("Power", power, MetricDataType.Float),
                    new Metric("PowerFactor", 0.95f - (float)(Random.Shared.NextDouble() * 0.1), MetricDataType.Float)
                };

                // Publish updates
                await publisher.PublishNodeMetricsAsync(nodeUpdates);
                await publisher.PublishDeviceMetricsAsync("Motor1", motorUpdates);

                Log.Information("Update {Index}: Temp={Temp:F1}°C, Runtime={Runtime}s, Current={Current:F1}A, Power={Power:F0}W",
                    i + 1, temp, runtime, current, power);
            }

            // Scenario 7: Test rebirth functionality
            Log.Information("\n=== Scenario 7: Testing rebirth functionality ===");
            Log.Information("Triggering rebirth - all metrics will be republished");
            await publisher.HandleRebirthAsync();
            Log.Information("Rebirth complete - all births have been republished");

            // Scenario 8: Device death and removal
            Log.Information("\n=== Scenario 8: Device death ===");
            await Task.Delay(2000);

            Log.Information("Removing Motor1...");
            await publisher.PublishDeviceDeathAsync("Motor1");

            // Try to get metrics for removed device
            var removedMetrics = publisher.GetAllDeviceMetrics("Motor1");
            Log.Information("Metrics for Motor1 after death: {Count}", removedMetrics.Count());

            // Final summary
            Log.Information("\n=== Final Summary ===");
            Log.Information("Node metrics in cache:");
            foreach (var metric in publisher.GetAllNodeMetrics())
            {
                Log.Information("  {Name}: {Value}", metric.Name, metric.Value);
            }

            Log.Information("\nDevice Sensor1 metrics in cache:");
            foreach (var metric in publisher.GetAllDeviceMetrics("Sensor1"))
            {
                Log.Information("  {Name}: {Value}", metric.Name, metric.Value);
            }

            // Disconnect
            Log.Information("\nDisconnecting...");
            await publisher.DisconnectAsync();
            Log.Information("Test completed successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in test program");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}