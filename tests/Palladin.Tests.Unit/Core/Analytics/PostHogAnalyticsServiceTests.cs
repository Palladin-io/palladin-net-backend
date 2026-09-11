using Palladin.Core.Analytics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using PostHog;

namespace Palladin.Tests.Unit.Core.Analytics;

public sealed class PostHogAnalyticsServiceTests
{
    private readonly IPostHogClient _client = Substitute.For<IPostHogClient>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 9, 11, 12, 0));

    [Fact]
    public void When_BusinessEventIsCaptured_Then_ItNeedsNoConsentAndDoesNotCreatePersonProfile()
    {
        // Given
        var service = new PostHogAnalyticsService(_client, _clock);

        // When
        service.CaptureEvent("account-id", "identity", "user-signed-up");

        // Then
        _client.Received(1).Capture("account-id", "be:identity:user-signed-up",
            Arg.Is<Dictionary<string, object>>(properties => properties.Count == 3
                && (bool)properties["$process_person_profile"] == false
                && (bool)properties["$geoip_disable"]
                && (string)properties["be_event_sent_at"] == _clock.GetCurrentInstant().ToString()));
    }

    [Fact]
    public void When_PropertiesContainClientTelemetryOrSensitiveValues_Then_OnlyAllowedBusinessPropertiesAreSent()
    {
        // Given
        var service = new PostHogAnalyticsService(_client, _clock);
        var properties = new Dictionary<string, object>
        {
            ["language"] = "pl", ["count"] = 3,
            ["$session_id"] = "client-session", ["correlation_id"] = "trace-id",
            ["$user_agent"] = "client-agent", ["$app_version"] = "client-version",
            ["$feature/experiment"] = "variant", ["$ip"] = "192.0.2.1",
            ["email"] = "test@example.com", ["token"] = "test-token",
            ["name"] = "test-title", ["vault_id"] = "vault-id", ["entry_id"] = "entry-id",
            // The import API accepts a free-form format label; it is not analytics-safe.
            ["format"] = "private-import-name@example.com",
            ["$set"] = new { email = "test@example.com" }, ["$process_person_profile"] = true,
        };

        // When
        service.CaptureEvent("opaque-id", "identity", "waitlist-joined", properties);

        // Then
        _client.Received(1).Capture("opaque-id", "be:identity:waitlist-joined",
            Arg.Is<Dictionary<string, object>>(payload => payload.Count == 5
                && (string)payload["language"] == "pl" && (int)payload["count"] == 3
                && (bool)payload["$process_person_profile"] == false));
        properties.ContainsKey("be_event_sent_at").ShouldBeFalse();
        ((bool)properties["$process_person_profile"]).ShouldBeTrue();
    }
}
