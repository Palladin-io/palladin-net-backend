using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class WaitlistDeveloperBenefitTests
{
    [Fact]
    public void When_VerifiedWaitlistEntryPredatesVerifiedAccount_Then_BenefitLastsOneCalendarMonth()
    {
        var waitlistJoinedAt = Instant.FromUtc(2026, 1, 1, 10, 0);
        var accountCreatedAt = Instant.FromUtc(2026, 1, 15, 10, 0);
        var activatedAt = Instant.FromUtc(2026, 1, 31, 10, 0);
        var expectedEndsAt = Instant.FromUtc(2026, 2, 28, 10, 0);
        var user = CreateVerifiedPasswordUser(accountCreatedAt);
        var entry = CreateVerifiedEntry(waitlistJoinedAt);
        user.FetchEvents();

        var endsAt = entry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, activatedAt);
        user.ActivateWaitlistDeveloperBenefit(activatedAt, endsAt!.Value);

        entry.DeveloperBenefitUserId.ShouldBe(user.Id);
        entry.DeveloperBenefitStartedAt.ShouldBe(activatedAt);
        entry.DeveloperBenefitEndsAt.ShouldBe(expectedEndsAt);
        user.WaitlistDeveloperBenefitStartedAt.ShouldBe(activatedAt);
        user.WaitlistDeveloperBenefitEndsAt.ShouldBe(expectedEndsAt);
        user.EffectivePlan(PlanType.Basic, activatedAt).ShouldBe(PlanType.Pro);

        var @event = user.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<WaitlistDeveloperBenefitActivatedEvent>();
        @event.StartsAt.ShouldBe(activatedAt);
        @event.EndsAt.ShouldBe(expectedEndsAt);
    }

    [Fact]
    public void When_WaitlistEntryIsPendingOrCreatedAfterAccount_Then_BenefitIsRejected()
    {
        var accountCreatedAt = Instant.FromUtc(2026, 8, 25, 10, 0);
        var activatedAt = accountCreatedAt + Duration.FromHours(2);
        var user = CreateVerifiedPasswordUser(accountCreatedAt);
        var pendingEntry = WaitlistEntry.Join(
            Guid.NewGuid(), user.Email, "en", "pending", "pending-hash",
            Duration.FromDays(1), accountCreatedAt - Duration.FromDays(1));
        var lateEntry = CreateVerifiedEntry(accountCreatedAt + Duration.FromMinutes(1));

        pendingEntry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, activatedAt).ShouldBeNull();
        lateEntry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, activatedAt).ShouldBeNull();
    }

    [Fact]
    public void When_BenefitWasAlreadyClaimed_Then_ItCannotBeClaimedAgainOrExtended()
    {
        var waitlistJoinedAt = Instant.FromUtc(2026, 8, 1, 10, 0);
        var accountCreatedAt = Instant.FromUtc(2026, 8, 2, 10, 0);
        var firstActivation = Instant.FromUtc(2026, 8, 25, 10, 0);
        var user = CreateVerifiedPasswordUser(accountCreatedAt);
        var entry = CreateVerifiedEntry(waitlistJoinedAt);

        var firstEndsAt = entry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, firstActivation);
        var secondEndsAt = entry.ClaimDeveloperBenefit(
            user.Id,
            user.CreatedAt,
            firstActivation + Duration.FromDays(10));

        firstEndsAt.ShouldBe(Instant.FromUtc(2026, 9, 25, 10, 0));
        secondEndsAt.ShouldBeNull();
        entry.DeveloperBenefitStartedAt.ShouldBe(firstActivation);
        entry.DeveloperBenefitEndsAt.ShouldBe(firstEndsAt);
    }

    [Fact]
    public void When_BenefitExpiresOrOrganizationHasHigherPlan_Then_EffectivePlanIsCorrect()
    {
        var waitlistJoinedAt = Instant.FromUtc(2026, 8, 1, 10, 0);
        var accountCreatedAt = Instant.FromUtc(2026, 8, 2, 10, 0);
        var activatedAt = Instant.FromUtc(2026, 8, 25, 10, 0);
        var user = CreateVerifiedPasswordUser(accountCreatedAt);
        var entry = CreateVerifiedEntry(waitlistJoinedAt);
        var endsAt = entry.ClaimDeveloperBenefit(user.Id, user.CreatedAt, activatedAt)!.Value;
        user.ActivateWaitlistDeveloperBenefit(activatedAt, endsAt);

        user.EffectivePlan(PlanType.Basic, activatedAt).ShouldBe(PlanType.Pro);
        user.EffectivePlan(PlanType.Enterprise, activatedAt).ShouldBe(PlanType.Enterprise);
        user.EffectivePlan(PlanType.Basic, endsAt).ShouldBe(PlanType.Basic);
        user.ActiveWaitlistDeveloperBenefitEndsAt(endsAt).ShouldBeNull();
    }

    private static WaitlistEntry CreateVerifiedEntry(Instant joinedAt)
    {
        var entry = WaitlistEntry.Join(
            Guid.NewGuid(), "waitlisted@example.com", "pl", "token", "token-hash",
            Duration.FromDays(30), joinedAt);
        entry.Verify(joinedAt + Duration.FromMinutes(1));
        return entry;
    }

    private static User CreateVerifiedPasswordUser(Instant createdAt)
    {
        var user = User.RegisterWithPassword(
            Guid.NewGuid(),
            "waitlisted@example.com",
            "Waitlisted User",
            "pl",
            Guid.NewGuid(),
            Permission.VaultManage,
            new byte[16],
            new byte[16],
            new byte[32],
            new byte[32],
            new byte[32],
            null,
            "web",
            createdAt);
        user.MarkEmailVerified(createdAt + Duration.FromMinutes(1));
        return user;
    }
}
