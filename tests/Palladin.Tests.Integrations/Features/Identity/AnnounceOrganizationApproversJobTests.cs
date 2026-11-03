using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Identity.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class AnnounceOrganizationApproversJobTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ExistingMembershipsHaveNoVaultReplica_Then_JobBackfillsEligibilityIdempotently()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        (await ReplicaAsync(organization.Id)).ShouldBeEmpty();

        // When
        await RunJobAsync();
        var first = await WaitForReplicaAsync(organization.Id, 2);
        await RunJobAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var second = await ReplicaAsync(organization.Id);

        // Then
        first.Single(x => x.UserId == owner.Id).IsApprover.ShouldBeTrue();
        first.Single(x => x.UserId == member.Id).IsApprover.ShouldBeTrue();
        second.ShouldBe(first);
    }

    private async Task RunJobAsync()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AnnounceOrganizationApproversJob>()
            .ExecuteAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<(Guid UserId, bool IsApprover)>> ReplicaAsync(Guid organizationId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var rows = await db.OrganizationApprovers.Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.UserId)
            .Select(x => new { x.UserId, x.IsApprover })
            .ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(x => (x.UserId, x.IsApprover)).ToList();
    }

    private async Task<List<(Guid UserId, bool IsApprover)>> WaitForReplicaAsync(Guid organizationId, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var rows = await ReplicaAsync(organizationId);
            if (rows.Count >= count || DateTime.UtcNow > deadline)
            {
                return rows;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
