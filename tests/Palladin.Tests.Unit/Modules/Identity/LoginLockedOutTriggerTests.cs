using MassTransit;
using NodaTime;
using NSubstitute;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Triggers;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class LoginLockedOutTriggerTests
{
    [Fact]
    public async Task When_LockoutIsAnalyzed_Then_RecipientAndSourceIpsStayOutOfAnalytics()
    {
        var analytics = Substitute.For<IAnalyticsService>();
        var occurredAt = Instant.FromUtc(2026, 8, 22, 12, 3);
        var @event = new LoginLockedOutEvent(
            Guid.NewGuid(),
            new string('c', 64),
            ["198.51.100.10", "203.0.113.11"],
            Guid.NewGuid(),
            "member@example.com",
            "pl",
            4,
            5,
            15,
            occurredAt + Duration.FromMinutes(15),
            occurredAt);
        var context = Substitute.For<ConsumeContext<LoginLockedOutEvent>>();
        context.Message.Returns(@event);

        await new OnLoginLockedOutAnalytics(analytics).Consume(context);

        analytics.Received(1).CaptureEvent(
            @event.EmailHash,
            "identity",
            "login-locked-out",
            null);
        analytics.ReceivedCalls().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task When_KnownAccountIsLocked_Then_BrandedAlertIsPublishedWithoutUnlockLink()
    {
        var targetUserId = Guid.NewGuid();
        var occurredAt = Instant.FromUtc(2026, 8, 22, 12, 3);
        var alertBucket = occurredAt.ToUnixTimeSeconds() / 900;
        var @event = new LoginLockedOutEvent(
            Guid.NewGuid(),
            new string('c', 64),
            ["198.51.100.10", "203.0.113.11"],
            targetUserId,
            "member@example.com",
            "pl",
            4,
            5,
            15,
            occurredAt + Duration.FromMinutes(15),
            occurredAt);
        var context = Substitute.For<ConsumeContext<LoginLockedOutEvent>>();
        context.Message.Returns(@event);

        await new OnLoginLockedOutEmail().Consume(context);

        await context.Received(1).Publish(
            Arg.Is<SendEmailCommand>(command =>
                command.Email == "member@example.com"
                && command.Template == EmailTemplates.LoginLockoutAlert
                && command.Language == "pl"
                && command.Model["attemptCount"] == "4"
                && command.Model["windowMinutes"] == "5"
                && command.Model["ipAddresses"] == "198.51.100.10, 203.0.113.11"
                && command.Model["occurredAt"] == "2026-08-22 12:03 UTC"
                && command.Model["lockedUntil"] == "2026-08-22 12:18 UTC"
                && !command.Model.ContainsKey("unlockUrl")
                && !command.Model.ContainsKey("baseUrl")
                && command.IdempotencyKey == $"identity:login-lockout:{targetUserId:N}:{alertBucket}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_UnknownAccountIsLocked_Then_NoEmailIsPublished()
    {
        var occurredAt = Instant.FromUtc(2026, 8, 22, 12, 3);
        var @event = new LoginLockedOutEvent(
            Guid.NewGuid(),
            new string('c', 64),
            ["198.51.100.10"],
            null,
            null,
            null,
            4,
            5,
            15,
            occurredAt + Duration.FromMinutes(15),
            occurredAt);
        var context = Substitute.For<ConsumeContext<LoginLockedOutEvent>>();
        context.Message.Returns(@event);

        await new OnLoginLockedOutEmail().Consume(context);

        await context.DidNotReceiveWithAnyArgs().Publish(
            Arg.Any<SendEmailCommand>(),
            Arg.Any<CancellationToken>());
    }
}
