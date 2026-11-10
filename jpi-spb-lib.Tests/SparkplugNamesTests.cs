namespace SparkplugB.Publisher.Tests;

/// <summary>Ignition's tag and folder naming rules (8.1 and 8.3), as SparkplugNames enforces them.</summary>
public class SparkplugNamesTests
{
    [Theory]
    [InlineData("CPU")]
    [InlineData("Disk C")]
    [InlineData("Disk C:")]          // ':' is allowed after the first character
    [InlineData("Pump-1 (East)")]
    [InlineData("O'Brien")]
    [InlineData("_hidden")]
    [InlineData("9Lives")]
    [InlineData("Température")]       // Unicode letters count as letters
    [InlineData("温度")]
    [InlineData("\U0001D49Cbc")]      // a letter outside the Basic Multilingual Plane
    public void ValidIds(string name) => Assert.True(SparkplugNames.IsValidId(name));

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]               // '.' stops MQTT Engine ingesting a Group ID
    [InlineData("a/b")]               // Sparkplug reserves + / #
    [InlineData("a+b")]
    [InlineData("a#b")]
    [InlineData("Usage %")]
    [InlineData("a&b")]
    [InlineData("-x")]                // allowed later, not first
    [InlineData(" x")]
    [InlineData("(x)")]
    [InlineData("hot\U0001F525")]     // an emoji is not a letter
    public void InvalidIds(string name) => Assert.False(SparkplugNames.IsValidId(name));

    [Theory]
    [InlineData("host.domain.local", "host_domain_local")]
    [InlineData("(1) Pump", "_(1) Pump")]  // Ignition's own convention: prefix '_'
    [InlineData("-x", "_-x")]
    [InlineData(".x", "_x")]
    [InlineData("a/b+c#d", "a_b_c_d")]
    [InlineData("\U0001F525 temp", "_ temp")] // one emoji is one character, so one '_'
    [InlineData("", "_")]
    [InlineData("Disk C:", "Disk C:")]
    public void MakeValidId(string name, string expected) => Assert.Equal(expected, SparkplugNames.MakeValidId(name));

    [Theory]
    [InlineData("CPU/Load", true)]
    [InlineData("Node Control/Rebirth", true)]
    [InlineData("Load", true)]
    [InlineData("a//b", false)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("Usage %", false)]
    [InlineData("a/.b", false)]
    public void MetricNames(string name, bool valid) => Assert.Equal(valid, SparkplugNames.IsValidMetricName(name));

    [Theory]
    [InlineData("Pump.1/Speed%", "Pump_1/Speed_")]
    [InlineData("/Pump//Speed/", "Pump/Speed")]
    [InlineData("", "_")]
    [InlineData("///", "_")]
    [InlineData("Node Control/Rebirth", "Node Control/Rebirth")]
    public void MakeValidMetricName(string name, string expected) =>
        Assert.Equal(expected, SparkplugNames.MakeValidMetricName(name));

    [Theory]
    [InlineData("host.domain.local")]
    [InlineData("  (weird) .name/with#all+of&it  ")]
    [InlineData("\U0001F525\U0001F525")]
    [InlineData("a\uD800b")]          // a lone surrogate (invalid UTF-16)
    public void MakeValid_AlwaysProducesAValidName_AndIsStable(string name)
    {
        var id = SparkplugNames.MakeValidId(name);
        Assert.True(SparkplugNames.IsValidId(id));
        Assert.Equal(id, SparkplugNames.MakeValidId(id));

        var metric = SparkplugNames.MakeValidMetricName(name);
        Assert.True(SparkplugNames.IsValidMetricName(metric));
        Assert.Equal(metric, SparkplugNames.MakeValidMetricName(metric));
    }
}
