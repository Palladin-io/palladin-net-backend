using NodaTime;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class LoginLockoutTests
{
    [Fact]
    public void When_FailureIsRecorded_Then_SafeAttemptEventIsRaised()
    {
        var now = Instant.FromUtc(2026, 8, 22, 12, 0);
        var attemptId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "member@example.com",
            "198.51.100.10",
            now);
        lockout.RecordFailure(
            5,
            Duration.FromMinutes(15),
            Duration.FromMinutes(15),
            attemptId,
            LoginFailureAttribution.Known(
                organizationId,
                userId,
                LoginAttemptFactor.Password,
                "pl",
                true),
            now);

        var @event = lockout.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<LoginAttemptFailedEvent>();
        @event.AttemptId.ShouldBe(attemptId);
        @event.OrganizationId.ShouldBe(organizationId);
        @event.TargetUserId.ShouldBe(userId);
        @event.Factor.ShouldBe(LoginAttemptFactor.Password);
        @event.IpAddress.ShouldBe("198.51.100.10");
        @event.EmailHash.Length.ShouldBe(64);
        @event.EmailHash.ShouldNotContain("member@example.com");
    }

    [Fact]
    public void When_EmptyStateIsReset_Then_VersionAdvancesAsAuthenticationFence()
    {
        var createdAt = Instant.FromUtc(2026, 8, 22, 12, 0);
        var resetAt = createdAt + Duration.FromSeconds(1);
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "member@example.com",
            "198.51.100.10",
            createdAt);

        lockout.Reset(resetAt);

        lockout.Version.ShouldBe(1u);
        lockout.FailedCount.ShouldBe(0);
        lockout.LockedUntil.ShouldBeNull();
        lockout.WindowStartedAt.ShouldBe(resetAt);
        lockout.UpdatedAt.ShouldBe(resetAt);
    }

    [Fact]
    public void When_ThresholdIsReached_Then_LockoutEventIsRaisedOncePerWindow()
    {
        var now = Instant.FromUtc(2026, 8, 22, 12, 0);
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "member@example.com",
            "198.51.100.10",
            now);
        var attribution = LoginFailureAttribution.Known(
            Guid.NewGuid(),
            Guid.NewGuid(),
            LoginAttemptFactor.Password,
            "pl",
            true);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            lockout.RecordFailure(
                5,
                Duration.FromMinutes(15),
                Duration.FromMinutes(15),
                Guid.NewGuid(),
                attribution,
                now);
        }

        lockout.RecordFailure(
            5,
            Duration.FromMinutes(15),
            Duration.FromMinutes(15),
            Guid.NewGuid(),
            attribution,
            now);
        var @event = lockout.FetchEvents()
            .OfType<LoginLockedOutEvent>()
            .ShouldHaveSingleItem()
            .ShouldBeOfType<LoginLockedOutEvent>();

        @event.EmailHash.ShouldNotContain("member@example.com");
        @event.IpAddress.ShouldBe("198.51.100.10");
        @event.RecipientEmail.ShouldBe("member@example.com");
        @event.PreferredLanguage.ShouldBe("pl");
        @event.AttemptCount.ShouldBe(5);
        @event.WindowMinutes.ShouldBe(15);
        @event.LockoutMinutes.ShouldBe(15);
        @event.LockedUntil.ShouldBe(now + Duration.FromMinutes(15));
        lockout.FetchEvents().ShouldBeEmpty();
    }

    [Fact]
    public void When_UnknownAccountThresholdIsReached_Then_RecipientFieldsRemainEmpty()
    {
        var now = Instant.FromUtc(2026, 8, 22, 12, 0);
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "unknown@example.com",
            "203.0.113.10",
            now);

        lockout.RecordFailure(
            1,
            Duration.FromMinutes(1),
            Duration.FromMinutes(15),
            Guid.NewGuid(),
            LoginFailureAttribution.Unknown(LoginAttemptFactor.Password),
            now);

        var @event = lockout.FetchEvents()
            .OfType<LoginLockedOutEvent>()
            .ShouldHaveSingleItem();
        @event.TargetUserId.ShouldBeNull();
        @event.RecipientEmail.ShouldBeNull();
        @event.PreferredLanguage.ShouldBeNull();
        @event.EmailHash.ShouldNotContain("unknown@example.com");
    }

    [Fact]
    public void When_UnverifiedAccountThresholdIsReached_Then_NoSecurityEmailRecipientIsExposed()
    {
        var now = Instant.FromUtc(2026, 8, 22, 12, 0);
        var targetUserId = Guid.NewGuid();
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "unverified@example.com",
            "203.0.113.10",
            now);

        lockout.RecordFailure(
            1,
            Duration.FromMinutes(1),
            Duration.FromMinutes(15),
            Guid.NewGuid(),
            LoginFailureAttribution.Known(
                Guid.NewGuid(),
                targetUserId,
                LoginAttemptFactor.Password,
                "pl",
                false),
            now);

        var @event = lockout.FetchEvents()
            .OfType<LoginLockedOutEvent>()
            .ShouldHaveSingleItem();
        @event.TargetUserId.ShouldBe(targetUserId);
        @event.RecipientEmail.ShouldBeNull();
        @event.PreferredLanguage.ShouldBeNull();
    }
}
