using System.Net;
using System.Text.Json;
using Palladin.Core.Security;
using Palladin.Module.Vault.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class GetVaultTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_VaultMember_GetsVault_Then_Returns200()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var (response, result) = await client
            .GETAsync<GetVaultEndpoint, GetVaultRequest, GetVaultResponse>(new GetVaultRequest { Id = vault.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Id.ShouldBe(vault.Id);
        result.OrganizationId.ShouldBe(organization.Id);
        result.ProtocolVersion.ShouldBe((ushort)2);
        result.MetadataRevision.ShouldBe("1");
        result.MetadataRevision.ShouldBe(result.MemberVaultMetadata.Descriptor.ResourceRevision);
        result.MemberVaultMetadata.VaultId.ShouldBe(vault.Id);
        result.MemberVaultKey.MemberId.ShouldBe(user.Id);
        result.MemberCount.ShouldBe(1);
        result.EntryCount.ShouldBe(0);
        result.ActiveGrantCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_VaultHasEntries_Then_EntryCountReflectsThem()
    {
        // Given — a vault with one entry
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id,
            EntryFaker.Create(vaultId: vault.Id, createdBy: user.Id));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var (response, result) = await client
            .GETAsync<GetVaultEndpoint, GetVaultRequest, GetVaultResponse>(new GetVaultRequest { Id = vault.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_VaultMember_GetsVault_Then_SerializesCanonicalMetadataRevisionAtTopLevel()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var response = await client.GetAsync($"api/vaults/{vault.Id}", TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var metadataRevision = body.RootElement.GetProperty("metadataRevision");
        metadataRevision.ValueKind.ShouldBe(JsonValueKind.String);
        metadataRevision.GetString().ShouldBe("1");
        metadataRevision.GetString().ShouldBe(body.RootElement
            .GetProperty("memberVaultMetadata")
            .GetProperty("descriptor")
            .GetProperty("resourceRevision")
            .GetString());
    }

    [Fact]
    public async Task When_NonMemberWithVaultManage_GetsVault_Then_Returns404()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);

        var admin = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(admin, Permission.VaultManage);

        // When
        var response = await client.GetAsync($"api/vaults/{vault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_NonMemberWithoutVaultManage_GetsVault_Then_Returns404()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);

        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(outsider, Permission.VaultCreate);

        // When
        var response = await client.GetAsync($"api/vaults/{vault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_AuthenticatedUser_GetsVaultFromOtherOrg_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (otherUser, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var otherVault = await apiFactory.Services.SeedVaultAsync(otherOrganization.Id, otherUser.Id);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync($"api/vaults/{otherVault.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
