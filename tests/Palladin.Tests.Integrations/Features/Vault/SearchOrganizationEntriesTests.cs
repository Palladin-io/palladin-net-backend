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

    [Fact]
    public async Task When_MemberHasMoreEncryptedIndexes_Then_OrgListingContinuesWithOpaqueCursor()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var firstEntry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var secondEntry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var (_, firstPage) = await client
            .GETAsync<SearchOrganizationEntriesEndpoint, SearchOrganizationEntriesRequest, SearchOrganizationEntriesResponse>(
                new SearchOrganizationEntriesRequest { PageSize = 1 });
        var (_, secondPage) = await client
            .GETAsync<SearchOrganizationEntriesEndpoint, SearchOrganizationEntriesRequest, SearchOrganizationEntriesResponse>(
                new SearchOrganizationEntriesRequest { PageSize = 1, Cursor = firstPage!.NextCursor });

        firstPage.NextCursor.ShouldNotBeNullOrWhiteSpace();
        secondPage!.Items.ShouldHaveSingleItem();
        secondPage.NextCursor.ShouldBeNull();
        new[] { firstPage.Items.Single().Id, secondPage.Items.Single().Id }
            .ShouldBe(new[] { firstEntry.Id, secondEntry.Id }, ignoreOrder: true);
    }
}
