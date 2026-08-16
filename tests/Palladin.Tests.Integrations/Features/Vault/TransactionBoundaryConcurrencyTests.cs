using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class TransactionBoundaryConcurrencyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_TwoVaultWritersUseTheSameMutationStamp_Then_TheStaleCommitConflicts()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var staleScope = apiFactory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var stale = staleScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var firstVault = await first.Vaults.SingleAsync(x => x.Id == vault.Id);
        var staleVault = await stale.Vaults.SingleAsync(x => x.Id == vault.Id);

        firstVault.FenceAccessMutation(user.Id, apiFactory.FakeClock.GetCurrentInstant());
        staleVault.FenceAccessMutation(user.Id, apiFactory.FakeClock.GetCurrentInstant());

        await first.CommitAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() =>
            stale.CommitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_TwoMembershipWritersUseTheSameOrganizationStamp_Then_TheStaleCommitConflicts()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var staleScope = apiFactory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var stale = staleScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var firstOrganization = await first.Organizations.SingleAsync(x => x.Id == organization.Id);
        var staleOrganization = await stale.Organizations.SingleAsync(x => x.Id == organization.Id);

        firstOrganization.FenceMembershipMutation();
        staleOrganization.FenceMembershipMutation();

        await first.CommitAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() =>
            stale.CommitAsync(TestContext.Current.CancellationToken));
    }
}
