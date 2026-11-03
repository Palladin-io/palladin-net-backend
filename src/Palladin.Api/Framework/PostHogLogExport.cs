using Palladin.Core.Analytics;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace Palladin.Api.Framework;

internal sealed class PostHogLogsOptions
{
    public const string Position = "PostHog:Logs";

    public string[] SourceContexts { get; init; } = [];
}

// Exports only allow-listed log sources to PostHog Logs over OTLP. Application logs may carry user data
// elsewhere, so nothing is exported unless its source context is explicitly reviewed and listed.
internal static class PostHogLogExport
{
    public static LoggerConfiguration WriteToPostHogLogs(this LoggerConfiguration logger, IConfiguration configuration)
    {
        var postHog = configuration.GetSection(PostHogOptions.Position).Get<PostHogOptions>() ?? new PostHogOptions();
        var logs = configuration.GetSection(PostHogLogsOptions.Position).Get<PostHogLogsOptions>() ?? new PostHogLogsOptions();
        if (string.IsNullOrWhiteSpace(postHog.ProjectApiKey) || logs.SourceContexts.Length == 0)
        {
            return logger;
        }

        var sourceContexts = logs.SourceContexts.ToHashSet(StringComparer.Ordinal);
        return logger.WriteTo.Logger(export => export
            .Filter.ByIncludingOnly(logEvent => IsExported(logEvent, sourceContexts))
            .WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = $"{postHog.Host.TrimEnd('/')}/i/v1/logs";
                options.Protocol = OtlpProtocol.HttpProtobuf;
                options.Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {postHog.ProjectApiKey}" };
                options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "palladin-api" };
            }));
    }

    internal static bool IsExported(LogEvent logEvent, IReadOnlySet<string> sourceContexts) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value)
        && value is ScalarValue { Value: string sourceContext }
        && sourceContexts.Contains(sourceContext);
}
