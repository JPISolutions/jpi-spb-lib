using MQTTnet.Protocol;

namespace SparkplugB.Publisher.Tests.Infrastructure;

/// <summary>
/// Replays what the host received from the edge node and checks it against the Sparkplug 3.0 rules
/// that govern an edge node session. Every test ends with this, so each scenario is also checked for
/// the rules it was not written about.
/// </summary>
public static class SparkplugAssert
{
    public static void SessionsAreWellFormed(IReadOnlyList<HostMessage> messages)
    {
        var errors = new List<string>();

        var inSession = false;
        ulong currentBdSeq = 0;
        ulong? previousSessionBdSeq = null;
        ulong expectedSeq = 0;
        var birthBdSeqs = new HashSet<ulong>();
        var nodeDeclared = new HashSet<string>();
        var deviceDeclared = new Dictionary<string, HashSet<string>>();

        foreach (var m in messages)
        {
            void Fail(string why) => errors.Add($"{m}  <-- {why}");

            if (m.Kind is "NBIRTH" or "DBIRTH" && m.HistoricalMetrics.Count > 0)
                Fail("a birth carries current state; no metric in it may be historical");

            switch (m.Kind)
            {
                case "NCMD":
                case "DCMD":
                case "STATE":
                    continue; // sent by the host, not the edge node

                case "NDEATH":
                    if (m.Seq is not null) Fail("NDEATH must not include seq (tck-id-topics-ndeath-seq)");
                    if (m.Qos != MqttQualityOfServiceLevel.AtLeastOnce) Fail("NDEATH must be QoS 1 (tck-id-payloads-ndeath-will-message-qos)");
                    if (m.Payload.Metrics.Count != 1 || m.BdSeq is null) Fail("NDEATH must carry only the bdSeq metric (tck-id-topics-ndeath-payload)");
                    else if (!birthBdSeqs.Contains(m.BdSeq.Value)) Fail("NDEATH bdSeq matches no NBIRTH, so the host would ignore it");

                    // A late will from an older connection can arrive after a newer NBIRTH;
                    // only the death of the current session ends it.
                    if (m.BdSeq == currentBdSeq) inSession = false;
                    continue;

                case "NBIRTH":
                    if (m.Seq != 0) Fail("NBIRTH seq must be 0 (tck-id-topics-nbirth-seq-num)");
                    if (m.Qos != MqttQualityOfServiceLevel.AtMostOnce) Fail("NBIRTH must be QoS 0 (tck-id-topics-nbirth-mqtt)");
                    if (m.BdSeq is not { } bdSeq)
                    {
                        Fail("NBIRTH must include bdSeq (tck-id-payloads-nbirth-bdseq)");
                        continue;
                    }
                    if (bdSeq > 255) Fail("bdSeq must be 0-255");

                    var rebirth = m.Payload.Metrics.FirstOrDefault(x => x.Name == "Node Control/Rebirth");
                    if (rebirth is null || rebirth.Datatype != (uint)MetricDataType.Boolean || rebirth.BooleanValue)
                        Fail("NBIRTH must include 'Node Control/Rebirth' = false, Boolean (tck-id-topics-nbirth-rebirth-metric)");

                    var newConnection = !inSession || bdSeq != currentBdSeq;
                    if (newConnection && previousSessionBdSeq is { } previous && bdSeq != (previous + 1) % 256)
                        Fail($"bdSeq must increase by one per connection (previous {previous}) (tck-id-topics-nbirth-bdseq-increment)");

                    inSession = true;
                    currentBdSeq = bdSeq;
                    previousSessionBdSeq = bdSeq;
                    birthBdSeqs.Add(bdSeq);
                    expectedSeq = 1;
                    nodeDeclared = m.MetricNames.ToHashSet();
                    deviceDeclared.Clear(); // a new NBIRTH voids every device birth
                    continue;
            }

            // DBIRTH, DDATA, NDATA, DDEATH: inside a session, QoS 0, seq exactly one more than the last.
            if (!inSession) Fail("published outside a session (no NBIRTH since the connection started)");
            if (m.Qos != MqttQualityOfServiceLevel.AtMostOnce) Fail($"{m.Kind} must be QoS 0 (tck-id-topics-{m.Kind.ToLowerInvariant()}-mqtt)");
            if (m.Seq != expectedSeq) Fail($"seq must be {expectedSeq} (tck-id-payloads-sequence-num-incrementing)");
            expectedSeq = ((m.Seq ?? expectedSeq) + 1) % 256; // resync so one gap is reported once

            switch (m.Kind)
            {
                case "DBIRTH":
                    deviceDeclared[m.DeviceId!] = m.MetricNames.ToHashSet();
                    break;

                case "DDATA":
                    if (!deviceDeclared.TryGetValue(m.DeviceId!, out var declared))
                        Fail("DDATA for a device not birthed in this session (tck-id-payloads-ddata-order)");
                    else
                        foreach (var name in m.MetricNames.Where(n => !declared.Contains(n)))
                            Fail($"metric '{name}' was not declared in the DBIRTH (tck-id-operational-behavior-data-publish-dbirth)");
                    break;

                case "NDATA":
                    foreach (var name in m.MetricNames.Where(n => !nodeDeclared.Contains(n)))
                        Fail($"metric '{name}' was not declared in the NBIRTH (tck-id-operational-behavior-data-publish-nbirth)");
                    break;

                case "DDEATH":
                    if (!deviceDeclared.Remove(m.DeviceId!)) Fail("DDEATH for a device not birthed in this session");
                    break;
            }
        }

        Assert.True(errors.Count == 0,
            "Sparkplug violations:\n" + string.Join("\n", errors) +
            "\n\nEverything the host received:\n" + string.Join("\n", messages));
    }
}
