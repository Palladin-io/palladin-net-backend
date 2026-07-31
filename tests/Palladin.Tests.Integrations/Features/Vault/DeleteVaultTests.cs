using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class DeleteVaultTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_DeletesVault_Then_Returns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.DeleteAsync($"api/vaults/{vault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Vaults.AnyAsync(v => v.Id == vault.Id)).ShouldBeFalse();
        (await readContext.VaultMembers.AnyAsync(m => m.VaultId == vault.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AuthenticatedUser_DeletesVaultFromOtherOrg_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (otherUser, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var otherVault = await apiFactory.Services.SeedVaultAsync(otherOrganization.Id, otherUser.Id);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.DeleteAsync($"api/vaults/{otherVault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_DeletingDefaultVault_Then_Returns409()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id, isDefault: true);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.DeleteAsync($"api/vaults/{vault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Vaults.AnyAsync(v => v.Id == vault.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_UserMissingVaultManage_DeletesVault_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var response = await client.DeleteAsync($"api/vaults/{vault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
