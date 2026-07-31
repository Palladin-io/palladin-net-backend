using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class ListApiKeysTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserHasApiKeyReadPermission_Then_ReturnsOnlyOrgKeys()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();

        var (ownKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Own Key");
        await apiFactory.Services.SeedApiKeyAsync(otherOrganization.Id, name: "Other Org Key");

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, result) = await client
            .GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].ApiKeyId.ShouldBe(ownKey.Id);
        result.Items[0].Name.ShouldBe("Own Key");
        result.Items[0].Status.ShouldBe(ApiKeyStatus.Active);
        result.Items[0].ActiveAgentCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_KeyIsActive_Then_ReturnsKeySuffixAndCreatedByName()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id, displayName: "Ada Lovelace");
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            name: "Active Key",
            createdBy: user.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, result) = await client
            .GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].KeySuffix.ShouldBe(apiKey.KeySuffix);
        result.Items[0].CreatedByName.ShouldBe("Ada Lovelace");
        result.Items[0].RevokedByName.ShouldBeNull();
        result.Items[0].RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_KeyIsRevoked_Then_ReturnsRevokedByName()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id, displayName: "Grace Hopper");
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            name: "Revoked Key",
            createdBy: user.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey | Permission.ReadApiKey);
        (await client.DeleteAsync($"api/api-keys/{apiKey.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        var (response, result) = await client
            .GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].Status.ShouldBe(ApiKeyStatus.Revoked);
        result.Items[0].KeySuffix.ShouldBe(apiKey.KeySuffix);
        result.Items[0].CreatedByName.ShouldBe("Grace Hopper");
        result.Items[0].RevokedByName.ShouldBe("Grace Hopper");
        result.Items[0].RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_ActiveAgentsHaveUsedKey_Then_ReturnsCorrectCount()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (keyA, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Key A");
        var (keyB, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Key B");

        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyA.Id));
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyA.Id));
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyB.Id));

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, result) = await client
            .GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();

        var summaryA = result.Items.Single(x => x.ApiKeyId == keyA.Id);
        var summaryB = result.Items.Single(x => x.ApiKeyId == keyB.Id);

        summaryA.ActiveAgentCount.ShouldBe(2);
        summaryB.ActiveAgentCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_UserLacksApiKeyReadPermission_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var (response, _) = await client
            .GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
