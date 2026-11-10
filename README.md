# jpi-spb-lib

A .NET library for publishing MQTT Sparkplug B data as an edge node, built against the
[Sparkplug 3.0.0 specification](https://sparkplug.eclipse.org/specification/) and tested with
Ignition's MQTT Engine as the host application.

The package is `JPI.SparkplugB.Publisher`, and the current release is **0.2.0 (Alpha-0.2)**.

## What it handles for you

- **Sessions.** Every MQTT connection registers an NDEATH will and then publishes NBIRTH and a DBIRTH
  for every known device before any data. If the connection drops, the library reconnects with
  back-off and sends the full birth sequence again.
- **bdSeq and seq.**
  - bdSeq starts at 0, goes up by one for each connection the broker accepts, and wraps from 255 to 0.
    It is saved to disk so the count continues across restarts.
  - Every message except NDEATH carries a sequence number. One counter covers the whole node, and a
    birth sequence restarts it at 0.
  - The counter is shared under a lock, so data cannot slip into the middle of a birth sequence.
- **Declarations.** Data only ever carries metrics its birth declared.
  - Publishing a new metric name or data type triggers a full rebirth that carries the new metric.
  - Publishing data for a device that has not been birthed yet sends its DBIRTH first.
- **Clean stop.** `DisconnectAsync` and `Dispose` publish NDEATH before disconnecting. A clean MQTT
  disconnect makes the broker discard the will, so without this the host would keep showing the node
  online.
- **Rebirth requests.** A `Node Control/Rebirth` NCMD from the host republishes every birth.
- **Outages.** Publishing while disconnected does not throw. The library stores the values, and the
  next connection's births carry them. You can also have it keep the outage's samples and send them
  as history (see [History backfill](#history-backfill)).
- **Names.** Names are checked against Ignition's tag naming rules (see [Names](#names)).

## Requirements

- .NET 8 (`net8.0`). The library has no Windows-specific code and runs on Linux as well.
- An MQTT 3.1.1 broker. It depends on MQTTnet 4.3.7, Google.Protobuf and Serilog.

## Getting it

Reference the project directly:

```xml
<ProjectReference Include="..\jpi-spb-lib\jpi-spb-lib\jpi-spb-lib.csproj" />
```

Or use the package. A Release build writes `JPI.SparkplugB.Publisher.0.2.0.nupkg` to
`jpi-spb-lib/bin/Release/`.

## Quick start

```csharp
using SparkplugB.Publisher;

var config = new SparkplugConfiguration
{
    GroupId = "Plant1",
    EdgeNodeId = Environment.MachineName,
    MqttBroker = "broker.example.com",
};

using var publisher = new SparkplugPublisher(config);
publisher.SetLogger(Log.Logger); // optional: any Serilog ILogger; the default is Serilog's global logger

// Declare the births first. While disconnected these only store the metric sets, so the first
// connection publishes them as one birth sequence.
await publisher.PublishNodeBirthAsync(new[] { new Metric("Host Name", Environment.MachineName, MetricDataType.String) }, ct);
await publisher.PublishDeviceBirthAsync("CPU", new[] { new Metric("Load", 0.0, MetricDataType.Double) }, ct);

// Returns once the first attempt has finished. If the broker is unreachable, the library logs it,
// keeps retrying in the background, and publishes the births when it connects.
await publisher.ConnectAsync(ct);

while (!ct.IsCancellationRequested)
{
    await publisher.PublishDeviceMetricsAsync("CPU", new[] { new Metric("Load", ReadCpuLoad(), MetricDataType.Double) }, ct);
    await Task.Delay(TimeSpan.FromSeconds(30), ct);
}

await publisher.DisconnectAsync(); // publishes NDEATH, then disconnects
```

Stop whatever publishes data before you call `DisconnectAsync`, so that nothing follows the NDEATH.

## Publishing

| Method | What it does |
| --- | --- |
| `PublishNodeBirthAsync(metrics)` | Declares the node's complete metric set. When connected, it publishes a full birth sequence: NBIRTH, then every DBIRTH. |
| `PublishDeviceBirthAsync(deviceId, metrics)` | Declares a device's complete metric set. When connected, it publishes the device's DBIRTH. |
| `PublishNodeMetricsAsync(metrics)` | Publishes NDATA for the given metrics. A new name or data type triggers a rebirth. |
| `PublishDeviceMetricsAsync(deviceId, metrics)` | Publishes DDATA. A device that has not been birthed gets its DBIRTH first. |
| `PublishDeviceDeathAsync(deviceId)` | Publishes DDEATH and forgets the device, so later connections do not birth it. Publishing data for the device brings it back. Sparkplug requires this when the node loses a device. |
| `PublishChangedMetricsAsync()` | Publishes only the stored metrics that changed since the last call. |
| `HandleRebirthAsync()` | Republishes every birth, as a host's rebirth request does. |

Births carry each metric's current value. To add a metric, just publish it. NDEATH tells the host that
every device under the node is offline, so a stop needs no DDEATHs.

### Values

Each metric's declared `MetricDataType` decides how its value is encoded. The value can be any
convertible .NET type:

- **Signed integers** go out in two's complement, so negative values work.
- **Out of range** values for the declared type are rejected. For example, 300 as a `UInt8` throws an
  `ArgumentException` that names the metric.
- **`null`** is sent as Sparkplug's `is_null`.
- **Booleans** accept `bool`, numbers (non-zero is `true`), and `"true"`/`"false"`/`"1"`/`"0"`.
- **`DateTime`** is milliseconds since 1970, so earlier dates are rejected.
- **Conversions** use the invariant culture, so a machine's regional settings cannot change a value.
- **Not supported:** `DataSet`, `Template` and `Unknown` throw `NotSupportedException`.

A value that cannot be encoded is rejected before it is stored, so it can never break a later birth.

## History backfill

This is off by default. Set `BackfillEnabled = true` to keep the samples published during an outage,
up to `BackfillMaxSamples` (default 10,000), in memory. After the next birth sequence and before any
live data, the library sends them in this order:

1. The births carry each metric's latest value as current state.
2. Every earlier sample follows, flagged `is_historical`, oldest first, with its original timestamp,
   at most 500 metrics per message.
3. Live data resumes.

Some details:

- **When the buffer fills**, the oldest samples are dropped, and a warning is logged once per outage.
- **Samples that are no longer declared are left out.** That covers a device retired during the
  outage, and a metric whose type changed.
- **A restart during an outage loses the buffer.**

In Ignition, historical samples go to the tag historian and never overwrite a tag's current value.
MQTT Engine needs its *Store Historical Events* setting and a history provider for this.

To publish your own historical samples, set `Metric.IsHistorical`. They never replace the stored
current value.

## Names

Group, edge node and device IDs become folders in an Ignition host's tag tree, and each `/`-separated
part of a metric name becomes a folder or a tag. All of them must follow Ignition's naming rules (8.1
and 8.3):

- **The first character** must be a letter, a digit or `_`.
- **Every later character** must be a letter, a digit, `_`, a space, or one of `' - : ( )`.

These rules also exclude the `+`, `/` and `#` that Sparkplug reserves in IDs. A `.` in a Group ID is
known to stop MQTT Engine ingesting a node.

What happens to a name that breaks the rules depends on `RenameInvalidNames`:

| | `true` (the default) | `false` |
| --- | --- | --- |
| Group or edge node ID | Published under its valid form, with a warning | Throws `ArgumentException`: a configuration error |
| Device ID or metric name | Published under its valid form, with a warning | Left out, as if it were not in the set, with an error logged |

- **The valid form** replaces each character that is not allowed with `_`. A name that starts with a
  character allowed only later, such as `(1) Pump`, gets a `_` prefix, as Ignition itself does.
- **Each name is logged only once.**
- **Two names that end up the same** (for example `Temp.1` and `Temp_1`) are not merged into one tag.
  The first keeps the name, and the second is left out with an error.
- **To clean names yourself**, use `SparkplugNames.IsValidId`, `IsValidMetricName`, `MakeValidId` and
  `MakeValidMetricName`.
- **`GetDeviceMetric` and `GetNodeMetric`** accept either your name or the published one.

## Configuration

`SparkplugConfiguration`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `GroupId` | `DefaultGroup` | Sparkplug group ID (required) |
| `EdgeNodeId` | `DefaultNode` | Edge node ID (required). It is read when `ConnectAsync` is called, so it can be set after constructing the publisher. |
| `MqttBroker` / `MqttPort` | `localhost` / `1883` | Broker address |
| `Username` / `Password` | none | Optional credentials |
| `UseTls` | `false` | TLS 1.2 to the broker |
| `KeepAliveSeconds` | `60` | MQTT keep-alive |
| `ReconnectMinDelaySeconds` / `ReconnectMaxDelaySeconds` | `1` / `60` | Reconnect back-off. The delay doubles on each failure, up to the maximum. |
| `ConnectTimeoutSeconds` | `10` | Time limit for one connection attempt |
| `BdSeqFilePath` | OS state folder | Where bdSeq is saved; see below |
| `RenameInvalidNames` | `true` | See [Names](#names) |
| `BackfillEnabled` / `BackfillMaxSamples` | `false` / `10000` | See [History backfill](#history-backfill) |

By default each edge node keeps its own bdSeq file, `<GroupId>.<EdgeNodeId>.bdseq`:

- **On Windows** it is in `%ProgramData%\JPI Solutions\Sparkplug`.
- **On Linux** it is in `$STATE_DIRECTORY` (set by systemd's `StateDirectory=`), otherwise in
  `$XDG_STATE_HOME/jpi-sparkplug`, otherwise in `~/.local/state/jpi-sparkplug`.

A relative `BdSeqFilePath` is resolved against the application folder. If the file cannot be read or
written, a warning is logged and the node keeps publishing.

## Building and testing

```shell
dotnet build -c Release
dotnet test
```

The tests run the publisher against an in-process MQTT broker. A TCP proxy in front of it simulates
network loss and a broker that is down. Every scenario finishes by checking everything a host received
against the Sparkplug session rules: sequence numbers, the bdSeq pairing, QoS, birth-before-data and
declared metrics.

## Changes

### 0.2.0 (Alpha-0.2)

Behaviour changes to check when upgrading:

- `ConnectAsync` no longer throws when the broker is unreachable. It keeps retrying in the background.
  Calling it twice is harmless.
- Publish methods no longer throw while disconnected.
- `PublishNodeBirthAsync` and `PublishDeviceBirthAsync` declare the complete metric set, replacing the
  previous one rather than adding to it.
- A new metric name or data type triggers a rebirth, and data for a device that has not been birthed
  sends its DBIRTH first.
- `null` is sent as `is_null`. Out-of-range values throw `ArgumentException`, and unsupported data types
  throw `NotSupportedException`.
- Names are checked against Ignition's rules and renamed by default.
- bdSeq starts at 0, wraps at 255, and lives in a per-node file in the OS state folder. On upgrade the
  old file is not read, so bdSeq starts at 0 once. Hosts do not notice.
- The target framework is now `net8.0`; it was `net8.0-windows8.0`.

Fixed:

- NDEATH is now published on a clean stop and on `Dispose`. Before, the host kept the node online.
- The will and the NBIRTH now carry the same bdSeq. Before, the host ignored the death after a crash.
- After a reconnect, every DBIRTH is republished, and no queued data from the old connection goes
  ahead of the births.
- Births and data use QoS 0; only NDEATH uses QoS 1, as the specification requires.
- Negative integers are encoded correctly, and `UInt32`/`UInt64` accept any integer type.
- A rebirth request without a data type is honoured. The specification says hosts should not send one.

Added: history backfill, `Metric.IsHistorical`, `SparkplugNames`, the reconnect, timeout, bdSeq-path,
naming and backfill settings, and a test suite.

### 0.1.0 (Alpha-0.1)

First release.

## License

MIT
