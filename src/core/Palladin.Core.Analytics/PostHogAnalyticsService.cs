using NodaTime;
using PostHog;

namespace Palladin.Core.Analytics;

public sealed class PostHogAnalyticsService(IPostHogClient postHogClient, IClock clock) : IAnalyticsService
{
    private static readonly HashSet<string> AllowedProperties =
    [
        "result_count", "grant_type", "method", "remaining_uses", "count",
        "approved_type", "expiry_source", "ttl_seconds", "query_limit", "duration_active_seconds",
        "revoked_by_system", "revision", "member_count", "reason", "code", "filters_used",
        "plan", "event_type", "success_count", "failure_count", "platform", "provider",
        "is_new_user", "language", "old_role_count", "new_role_count", "occurred_at",
    ];

    public void CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null)
    {
        var payload = properties?.Where(pair => AllowedProperties.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, object>();
        payload["be_event_sent_at"] = clock.GetCurrentInstant().ToString();
        payload["$process_person_profile"] = false;
        payload["$geoip_disable"] = true;
        postHogClient.Capture(distinctId, $"be:{module}:{eventName}", payload);
    }
}
