using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Palladin.Tests.Integrations.Shared;

// Receives every Serilog event of the test host so tests can assert that no secret material is logged.
public sealed class CapturedLogSink : ILogEventSink
{
    private static readonly ConcurrentQueue<string> Events = new();

    public static IReadOnlyCollection<string> Snapshot() => Events.ToArray();

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        logEvent.RenderMessage(writer);
        foreach (var property in logEvent.Properties)
        {
            writer.Write(' ');
            writer.Write(property.Key);
            writer.Write('=');
            property.Value.Render(writer);
        }

        if (logEvent.Exception is not null)
        {
            writer.Write(' ');
            writer.Write(logEvent.Exception);
        }

        Events.Enqueue(writer.ToString());
    }
}
