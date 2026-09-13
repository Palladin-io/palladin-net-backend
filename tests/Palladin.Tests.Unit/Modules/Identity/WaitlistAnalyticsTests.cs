using MassTransit;
using NodaTime;
using NSubstitute;
using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Triggers;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class WaitlistAnalyticsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 11, 12, 0);

    [Fact]
    public async Task When_LinkIsReissued_Then_EmailRequestIsUpdatedWithoutAnotherJoinMetric()
    {
        // Given
        var entry = WaitlistEntry.Join(Guid.NewGuid(), "test@example.com", "pl", "first", "first-hash", Duration.FromDays(1), Now);
        var joined = entry.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<WaitlistUpsertedEvent>();
        var analytics = Substitute.For<IAnalyticsService>();
        var trigger = new OnWaitlistUpsertedAnalytics(analytics);

        // When
        entry.ReissueToken("second", "second-hash", Duration.FromDays(1), Now + Duration.FromMinutes(10));
        var reissued = entry.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<WaitlistUpsertedEvent>();
        var context = Substitute.For<ConsumeContext<WaitlistUpsertedEvent>>();
        context.Message.Returns(joined);
        await trigger.Consume(context);
        context.Message.Returns(reissued);
        await trigger.Consume(context);

        // Then
        joined.Change.ShouldBe(EntityChange.Created);
        reissued.Change.ShouldBe(EntityChange.Updated);
        reissued.Token.ShouldBe("second");
        analytics.Received(1).CaptureEvent(entry.Id.ToString(), "identity", "waitlist-joined",
            Arg.Is<Dictionary<string, object>>(properties => properties.Count == 2
                && (string)properties["language"] == "pl" && (string)properties["occurred_at"] == Now.ToString()));
    }

    [Fact]
    public void When_VerificationIsRepeated_Then_OriginalOutcomeAndTimestampArePreserved()
    {
        // Given
        var entry = WaitlistEntry.Join(Guid.NewGuid(), "test@example.com", "en", "token", "hash", Duration.FromDays(1), Now);
        entry.FetchEvents();

        // When
        entry.Verify(Now + Duration.FromMinutes(1));
        entry.Verify(Now + Duration.FromMinutes(2));

        // Then
        entry.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<WaitlistVerifiedEvent>()
            .OccurredAt.ShouldBe(Now + Duration.FromMinutes(1));
        entry.VerifiedAt.ShouldBe(Now + Duration.FromMinutes(1));
    }

    [Fact]
    public async Task When_MessageIsRedelivered_Then_LogicalMetricIdentityAndOccurrenceTimeStayStable()
    {
        // Given
        var analytics = Substitute.For<IAnalyticsService>();
        var trigger = new OnWaitlistVerified(analytics);
        var context = Substitute.For<ConsumeContext<WaitlistVerifiedEvent>>();
        var message = new WaitlistVerifiedEvent(Guid.NewGuid(), Now);
        context.Message.Returns(message);

        // When
        await trigger.Consume(context);
        await trigger.Consume(context);

        // Then
        analytics.Received(2).CaptureEvent(message.EntryId.ToString(), "identity", "waitlist-verified",
            Arg.Is<Dictionary<string, object>>(properties => properties.Count == 1
                && (string)properties["occurred_at"] == Now.ToString()));
    }
}
