using NodaTime;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class WaitlistEntryBenefitTests
{
    private static readonly Instant PublicLaunch = Instant.FromUtc(2026, 12, 1, 0, 0);
    private static readonly Instant ClaimDeadline = Instant.FromUtc(2027, 6, 1, 0, 0);

    [Fact]
    public void When_AccountCreatedBeforePublicLaunch_Then_BenefitIsNotReserved()
    {
        // Given
        var entry = VerifiedEntry(Instant.FromUtc(2026, 7, 1, 0, 0));

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch - Duration.FromMilliseconds(1));

        // Then
        reserved.ShouldBeFalse();
        entry.BenefitUserId.ShouldBeNull();
    }

    [Fact]
    public void When_AccountCreatedAfterClaimDeadline_Then_BenefitIsNotReserved()
    {
        // Given
        var entry = VerifiedEntry(Instant.FromUtc(2026, 7, 1, 0, 0));

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, ClaimDeadline + Duration.FromMilliseconds(1));

        // Then
        reserved.ShouldBeFalse();
        entry.BenefitUserId.ShouldBeNull();
    }

    [Fact]
    public void When_WaitlistSignupWasNotBeforePublicLaunch_Then_BenefitIsNotReserved()
    {
        // Given
        var entry = VerifiedEntry(PublicLaunch);

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch + Duration.FromHours(1));

        // Then
        reserved.ShouldBeFalse();
        entry.BenefitUserId.ShouldBeNull();
    }

    [Fact]
    public void When_BenefitWasAlreadyReserved_Then_AnotherUserCannotReserveIt()
    {
        // Given
        var entry = VerifiedEntry(Instant.FromUtc(2026, 7, 1, 0, 0));
        var firstUserId = Guid.NewGuid();
        entry.TryReserveDeveloperBenefit(
                firstUserId, PublicLaunch, ClaimDeadline, 1,
                WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch)
            .ShouldBeTrue();

        // When
        entry.TryReserveDeveloperBenefit(
                Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
                WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch + Duration.FromDays(1))
            .ShouldBeFalse();

        // Then
        entry.BenefitUserId.ShouldBe(firstUserId);
    }

    [Fact]
    public void When_EmailIsConfirmedAfterLaunchBeforeAccountCreation_Then_BenefitIsReserved()
    {
        // Given
        var createdAt = PublicLaunch - Duration.FromMinutes(30);
        var entry = PendingEntry(createdAt);
        entry.Verify(PublicLaunch + Duration.FromMinutes(15));
        var accountCreatedAt = PublicLaunch + Duration.FromHours(1);

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, accountCreatedAt);

        // Then
        reserved.ShouldBeTrue();
        entry.BenefitStartsAt.ShouldBe(accountCreatedAt);
        entry.BenefitEndsAt.ShouldBe(Instant.FromUtc(2027, 1, 1, 1, 0));
    }

    [Fact]
    public void When_BenefitIsReserved_Then_IntegrationEventCarriesReservationUpdatedAt()
    {
        // Given
        var entry = VerifiedEntry(PublicLaunch - Duration.FromDays(1));
        var userId = Guid.NewGuid();
        var accountCreatedAt = PublicLaunch + Duration.FromHours(1);

        // When
        entry.TryReserveDeveloperBenefit(
                userId, PublicLaunch, ClaimDeadline, 1,
                WaitlistEntry.CurrentPromotionTermsVersion, accountCreatedAt)
            .ShouldBeTrue();

        // Then
        var @event = entry.FetchEvents()
            .OfType<WaitlistBenefitReservedEvent>()
            .ShouldHaveSingleItem();
        @event.UserId.ShouldBe(userId);
        @event.UpdatedAt.ShouldBe(accountCreatedAt);
    }

    [Fact]
    public void When_AccountIsCreatedAtClaimDeadline_Then_BenefitIsReserved()
    {
        // Given
        var entry = VerifiedEntry(PublicLaunch - Duration.FromDays(1));

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, ClaimDeadline);

        // Then
        reserved.ShouldBeTrue();
        entry.BenefitEndsAt.ShouldBe(Instant.FromUtc(2027, 7, 1, 0, 0));
    }

    [Fact]
    public void When_EmailWasNotConfirmed_Then_BenefitIsNotReserved()
    {
        // Given
        var entry = PendingEntry(PublicLaunch - Duration.FromDays(1));

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch);

        // Then
        reserved.ShouldBeFalse();
        entry.BenefitUserId.ShouldBeNull();
    }

    [Fact]
    public void When_SignupDidNotAcceptTheCurrentPromotionTerms_Then_BenefitIsNotReserved()
    {
        // Given
        var entry = PendingEntry(PublicLaunch - Duration.FromDays(1), "legacy-v0");
        entry.Verify(PublicLaunch - Duration.FromHours(1));

        // When
        var reserved = entry.TryReserveDeveloperBenefit(
            Guid.NewGuid(), PublicLaunch, ClaimDeadline, 1,
            WaitlistEntry.CurrentPromotionTermsVersion, PublicLaunch);

        // Then
        reserved.ShouldBeFalse();
        entry.BenefitUserId.ShouldBeNull();
    }

    private static WaitlistEntry VerifiedEntry(Instant createdAt)
    {
        var entry = PendingEntry(createdAt);
        entry.Verify(createdAt + Duration.FromMinutes(1));
        return entry;
    }

    private static WaitlistEntry PendingEntry(
        Instant createdAt,
        string promotionTermsVersion = WaitlistEntry.CurrentPromotionTermsVersion) =>
        WaitlistEntry.Join(
            Guid.NewGuid(),
            $"benefit-{Guid.NewGuid():N}@example.com",
            "en",
            WaitlistQualification.Create(
                "individual",
                "codex",
                null,
                "Rotate API credentials for release automation",
                null,
                true,
                "direct"),
            promotionTermsVersion,
            "token",
            new string('a', 64),
            Duration.FromHours(24),
            createdAt);
}
