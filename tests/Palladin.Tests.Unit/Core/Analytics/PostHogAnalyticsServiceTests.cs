using Palladin.Core.Analytics;
using Palladin.Core.Transport;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using PostHog;

namespace Palladin.Tests.Unit.Core.Analytics;

public sealed class PostHogAnalyticsServiceTests
{
    private readonly IPostHogClient _postHogClient = Substitute.For<IPostHogClient>();
    private readonly ITransportContext _transportContext = Substitute.For<ITransportContext>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 13, 12, 0, 0));

    private PostHogAnalyticsService CreateSut() => new(_postHogClient, _transportContext, _clock);

    [Fact]
    public void When_CaptureEvent_Then_PrefixesWithBeModuleEvent()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "identity", "user-signed-up");

        // Then
        _postHogClient.Received(1).Capture("user-1", "be:identity:user-signed-up", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public void When_SessionIdPresent_Then_EnrichesWithSessionId()
    {
        // Given
        var sut = CreateSut();
        _transportContext.SessionId.Returns("posthog-session-abc");
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p => (string)p["$session_id"] == "posthog-session-abc"));
    }

    [Fact]
    public void When_CorrelationIdPresent_Then_EnrichesWithCorrelationId()
    {
        // Given
        var sut = CreateSut();
        var correlationId = Guid.NewGuid();
        _transportContext.CorrelationId.Returns(correlationId);
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "vault", "entry-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:entry-created",
            Arg.Is<Dictionary<string, object>>(p => (Guid)p["correlation_id"] == correlationId));
    }

    [Fact]
    public void When_PlatformPresent_Then_EnrichesWithPlatform()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Platform.Returns("web");
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "identity", "user-signed-up");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:identity:user-signed-up",
            Arg.Is<Dictionary<string, object>>(p => (string)p["platform"] == "web"));
    }

    [Fact]
    public void When_PlatformAbsent_Then_DoesNotEnrichWithPlatform()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Platform.Returns((string?)null);
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "identity", "user-signed-up");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:identity:user-signed-up",
            Arg.Is<Dictionary<string, object>>(p => !p.ContainsKey("platform")));
    }

    [Fact]
    public void When_UserAgentHeaderPresent_Then_EnrichesWithUserAgent()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>
        {
            [CustomHeaders.UserAgentHeaderName] = "Palladin/web (Chrome 120.0; macOS)"
        });

        // When
        sut.CaptureEvent("user-1", "identity", "login");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:identity:login",
            Arg.Is<Dictionary<string, object>>(p => (string)p["$user_agent"] == "Palladin/web (Chrome 120.0; macOS)"));
    }

    [Fact]
    public void When_AppVersionHeaderPresent_Then_EnrichesWithAppVersion()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>
        {
            [CustomHeaders.AppVersionHeaderName] = "1.2.3"
        });

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p => (string)p["$app_version"] == "1.2.3"));
    }

    [Fact]
    public void When_AppBuildNumberHeaderPresent_Then_EnrichesWithAppBuild()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>
        {
            [CustomHeaders.AppBuildNumberHeaderName] = "42"
        });

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p => (string)p["$app_build"] == "42"));
    }

    [Fact]
    public void When_FeatureFlagsPresent_Then_EnrichesWithFeatureFlags()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>
        {
            ["x-ff-new-ui"] = "true",
            ["x-ff-dark-mode"] = "variant-a"
        });

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p =>
                (string)p["$feature/new-ui"] == "true" &&
                (string)p["$feature/dark-mode"] == "variant-a"));
    }

    [Fact]
    public void When_CaptureEvent_Then_AddsServerTimestamp()
    {
        // Given
        var sut = CreateSut();
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p => p.ContainsKey("be_event_sent_at")));
    }

    [Fact]
    public void When_NoTransportHeaders_Then_OnlyAddsTimestamp()
    {
        // Given
        var sut = CreateSut();
        _transportContext.SessionId.Returns((string?)null);
        _transportContext.CorrelationId.Returns((Guid?)null);
        _transportContext.Platform.Returns((string?)null);
        _transportContext.Headers.Returns(new Dictionary<string, string>());

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p =>
                p.Count == 1 &&
                p.ContainsKey("be_event_sent_at")));
    }

    [Fact]
    public void When_CallerPassesProperties_Then_MergesWithEnrichedProperties()
    {
        // Given
        var sut = CreateSut();
        _transportContext.SessionId.Returns("session-123");
        _transportContext.Headers.Returns(new Dictionary<string, string>());
        var callerProperties = new Dictionary<string, object>
        {
            ["vault_id"] = "vault-abc",
            ["entry_count"] = 5
        };

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created", callerProperties);

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p =>
                (string)p["$session_id"] == "session-123" &&
                (string)p["vault_id"] == "vault-abc" &&
                (int)p["entry_count"] == 5 &&
                p.ContainsKey("be_event_sent_at")));
    }

    [Fact]
    public void When_FullTransportContext_Then_EnrichesAllFields()
    {
        // Given
        var sut = CreateSut();
        var correlationId = Guid.NewGuid();
        _transportContext.SessionId.Returns("session-xyz");
        _transportContext.CorrelationId.Returns(correlationId);
        _transportContext.Headers.Returns(new Dictionary<string, string>
        {
            [CustomHeaders.UserAgentHeaderName] = "Palladin/mobile (iOS 18.2)",
            [CustomHeaders.AppVersionHeaderName] = "2.0.0",
            [CustomHeaders.AppBuildNumberHeaderName] = "100",
            ["x-ff-beta"] = "true"
        });

        // When
        sut.CaptureEvent("user-1", "vault", "vault-created");

        // Then
        _postHogClient.Received(1).Capture(
            "user-1",
            "be:vault:vault-created",
            Arg.Is<Dictionary<string, object>>(p =>
                (string)p["$session_id"] == "session-xyz" &&
                (Guid)p["correlation_id"] == correlationId &&
                (string)p["$user_agent"] == "Palladin/mobile (iOS 18.2)" &&
                (string)p["$app_version"] == "2.0.0" &&
                (string)p["$app_build"] == "100" &&
                (string)p["$feature/beta"] == "true" &&
                p.ContainsKey("be_event_sent_at")));
    }

    [Fact]
    public async Task When_IdentifyUser_Then_DelegatesToPostHogClient()
    {
        // Given
        var sut = CreateSut();
        var properties = new Dictionary<string, object> { ["email"] = "test@example.com" };

        // When
        await sut.IdentifyUser("user-1", properties);

        // Then
        await _postHogClient.Received(1).IdentifyAsync("user-1", properties, null, Arg.Any<CancellationToken>());
    }
}
