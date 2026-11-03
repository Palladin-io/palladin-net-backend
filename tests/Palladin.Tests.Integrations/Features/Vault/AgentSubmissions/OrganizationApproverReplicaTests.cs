using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

[Collection<ApiFactoryCollection>]
public sealed class OrganizationApproverReplicaTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_EligibilityMessagesArriveOutOfOrder_Then_LatestStateWinsAndTiesFailClosed()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var at = Instant.FromUnixTimeSeconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeSeconds());

        // When
        await ConsumeAsync(new UpsertOrganizationApproverCommand(organizationId, userId, true, at));
        var afterGrant = await IsApproverAsync(organizationId, userId);
        await ConsumeAsync(new UpsertOrganizationApproverCommand(organizationId, userId, false, at + Duration.FromSeconds(1)));
        await ConsumeAsync(new UpsertOrganizationApproverCommand(organizationId, userId, true, at));
        var afterStaleReplay = await IsApproverAsync(organizationId, userId);
        await ConsumeAsync(new UpsertOrganizationApproverCommand(organizationId, userId, true, at + Duration.FromSeconds(2)));
        await ConsumeAsync(new UpsertOrganizationApproverCommand(organizationId, userId, false, at + Duration.FromSeconds(2)));
        var afterTie = await IsApproverAsync(organizationId, userId);

        // Then
        afterGrant.ShouldBeTrue();
        afterStaleReplay.ShouldBeFalse();
        afterTie.ShouldBeFalse();
    }

    [Fact]
    public void When_OwnerMembershipIsCreatedAndRemovalRequested_Then_IdentityAnnouncesEligibilityTransitions()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1_800_000_000);
        var owner = OrganizationMember.CreateOwner(
            organizationId, Guid.NewGuid(), Role.CreateAdministrator(Guid.NewGuid(), organizationId, now), now);
        var member = OrganizationMember.Create(
            organizationId, Guid.NewGuid(), Role.CreateDefaultUser(Guid.NewGuid(), organizationId, now),
            "Member", "member@example.invalid", now);

        // When
        var ownerEvents = owner.FetchEvents();
        var memberEvents = member.FetchEvents();
        member.RequestRemoval(Guid.NewGuid(), owner.UserId, now + Duration.FromMinutes(1));
        var removalEvents = member.FetchEvents();

        // Then
        ownerEvents.OfType<UpsertOrganizationApproverCommand>().Single().IsApprover.ShouldBeTrue();
        memberEvents.OfType<UpsertOrganizationApproverCommand>().Single().IsApprover.ShouldBeFalse();
        var removal = removalEvents.OfType<UpsertOrganizationApproverCommand>().Single();
        removal.IsApprover.ShouldBeFalse();
        removal.UpdatedAt.ShouldBe(now + Duration.FromMinutes(1));
    }

    private Task ConsumeAsync(UpsertOrganizationApproverCommand command) =>
        apiFactory.ConsumeAsync<UpsertOrganizationApproverConsumer, UpsertOrganizationApproverCommand>(
            command, TestContext.Current.CancellationToken);

    private async Task<bool> IsApproverAsync(Guid organizationId, Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        return await db.OrganizationApprovers
            .Where(x => x.OrganizationId == organizationId && x.UserId == userId)
            .Select(x => x.IsApprover)
            .SingleAsync(TestContext.Current.CancellationToken);
    }
}
