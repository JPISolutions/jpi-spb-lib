using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace SparkplugB.Publisher.Tests.Infrastructure;

/// <summary>Captures log events so a test can count what the publisher logged.</summary>
public sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Events whose message template starts with the given text.</summary>
    public int Count(string templateStart) =>
        _events.Count(e => e.MessageTemplate.Text.StartsWith(templateStart, StringComparison.Ordinal));

    public ILogger CreateLogger() => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();
}
