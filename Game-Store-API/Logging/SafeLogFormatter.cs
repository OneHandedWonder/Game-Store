using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace Game_Store_API.Logging;

public sealed class SafeLogFormatter : ITextFormatter
{
    private static readonly HashSet<string> Allowed = ["SourceContext", "Event", "Method", "Route", "StatusCode",
        "ElapsedMs", "TraceId", "ActorId", "TargetId", "RemoteIp", "ErrorType", "Attempt", "MessageId", "Role", "UserId"];

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var properties = logEvent.Properties.Where(p => Allowed.Contains(p.Key) && p.Value is ScalarValue)
            .ToDictionary(p => p.Key, p => ((ScalarValue)p.Value).Value is System.Net.IPAddress ip ? ip.ToString() : ((ScalarValue)p.Value).Value);
        var source = properties.GetValueOrDefault("SourceContext") as string;
        var entry = new
        {
            timestamp = logEvent.Timestamp.UtcDateTime,
            level = logEvent.Level.ToString(),
            messageTemplate = source?.StartsWith("Game_Store_API.", StringComparison.Ordinal) == true
                ? logEvent.MessageTemplate.Text : "Framework event",
            properties,
            errorType = logEvent.Exception?.GetType().FullName,
            stackTrace = logEvent.Exception?.StackTrace
        };
        output.WriteLine(JsonSerializer.Serialize(entry));
    }
}
