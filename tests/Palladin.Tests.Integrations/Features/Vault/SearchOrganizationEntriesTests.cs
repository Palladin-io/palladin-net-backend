using System.Net;
using FastEndpoints;
using Palladin.Module.Vault.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class SearchOrganizationEntriesTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ListingLegacyEntries_Then_ReturnsOnlyMemberVaultRowsWithoutVaultMetadata()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var memberVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedEntryAsync(memberVault.Id, user.Id);

        var otherUser = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var foreignVault = await apiFactory.Services.SeedVaultAsync(organization.Id, otherUser.Id);
        await apiFactory.Services.SeedEntryAsync(foreignVault.Id, otherUser.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var (response, result) = await client
            .GETAsync<SearchOrganizationEntriesEndpoint, SearchOrganizationEntriesRequest, SearchOrganizationEntriesResponse>(
                new SearchOrganizationEntriesRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldHaveSingleItem().VaultId.ShouldBe(memberVault.Id);
        typeof(OrganizationEntryItem).GetProperty("VaultName").ShouldBeNull();
    }
}
