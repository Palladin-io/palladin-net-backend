using Palladin.Core.Transport;
using NodaTime;
using PostHog;

namespace Palladin.Core.Analytics;

public sealed class PostHogAnalyticsService(
    IPostHogClient postHogClient,
    ITransportContext transportContext,
    IClock clock) : IAnalyticsService
{
    public void CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null)
    {
        properties ??= new Dictionary<string, object>();

        EnrichWithTransportContext(properties);

        properties["be_event_sent_at"] = clock.GetCurrentInstant().ToString();

        postHogClient.Capture(distinctId, $"be:{module}:{eventName}", properties);
    }

    public async Task IdentifyUser(string distinctId, Dictionary<string, object>? properties = null,
        CancellationToken cancellationToken = default)
    {
        await postHogClient.IdentifyAsync(distinctId, properties, personPropertiesToSetOnce: null, cancellationToken);
    }

    private void EnrichWithTransportContext(Dictionary<string, object> properties)
    {
        if (transportContext.SessionId is { } sessionId)
        {
            properties["$session_id"] = sessionId;
        }

        if (transportContext.CorrelationId is { } correlationId)
        {
            properties["correlation_id"] = correlationId;
        }

        if (transportContext.Platform is { } platform)
        {
            properties["platform"] = platform;
        }

        if (transportContext.Headers.TryGetValue(CustomHeaders.UserAgentHeaderName, out var userAgent))
        {
            properties["$user_agent"] = userAgent;
        }

        if (transportContext.Headers.TryGetValue(CustomHeaders.AppVersionHeaderName, out var appVersion))
        {
            properties["$app_version"] = appVersion;
        }

        if (transportContext.Headers.TryGetValue(CustomHeaders.AppBuildNumberHeaderName, out var appBuild))
        {
            properties["$app_build"] = appBuild;
        }

        foreach (var header in transportContext.Headers.Where(h => h.Key.StartsWith(CustomHeaders.FeatureFlagPrefix)))
        {
            var flagName = header.Key[CustomHeaders.FeatureFlagPrefix.Length..];
            properties[$"$feature/{flagName}"] = header.Value;
        }
    }
}
