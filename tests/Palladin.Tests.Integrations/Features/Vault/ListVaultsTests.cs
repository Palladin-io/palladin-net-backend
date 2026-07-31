using System.Net;
using Palladin.Module.Vault.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class ListVaultsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_ListsVaults_Then_ReturnsOwnOrgVaults()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (otherUser, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();

        await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedVaultAsync(otherOrganization.Id, otherUser.Id);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListVaultsEndpoint, ListVaultsRequest, ListVaultsResponse>(new ListVaultsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Total.ShouldBe(2);
        result.Vaults.Count.ShouldBe(2);
        result.Vaults.ShouldAllBe(v => v.MemberCount == 1);
        result.Vaults.ShouldAllBe(v => v.EntryCount == 0);
        result.Vaults.ShouldAllBe(v => v.ActiveGrantCount == 0);
        result.Vaults.ShouldAllBe(v => v.VaultPrivateKeys.Count == 2);
        foreach (var vault in result.Vaults)
        {
            vault.DiscoveryKey.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task When_VaultHasEntries_Then_EntryCountReflectsThem()
    {
        // Given — one vault with two entries
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id,
            EntryFaker.Create(vaultId: vault.Id, createdBy: user.Id));
        await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id,
            EntryFaker.Create(vaultId: vault.Id, createdBy: user.Id));
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListVaultsEndpoint, ListVaultsRequest, ListVaultsResponse>(new ListVaultsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Vaults.Single(v => v.Id == vault.Id).EntryCount.ShouldBe(2);
    }

    [Fact]
    public async Task When_AuthenticatedUser_NoVaults_Then_ReturnsEmptyList()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListVaultsEndpoint, ListVaultsRequest, ListVaultsResponse>(new ListVaultsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Vaults.ShouldBeEmpty();
        result.Total.ShouldBe(0);
    }
}
