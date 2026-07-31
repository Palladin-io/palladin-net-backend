using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class RevokeApiKeyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_KeyExistsAndIsActive_Then_RevokesKeyAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.ApiKeys.FirstOrDefaultAsync(x => x.Id == apiKey.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(ApiKeyStatus.Revoked);
        persisted.RevokedBy.ShouldBe(user.Id);
        persisted.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_KeyIsAlreadyRevoked_Then_Returns204WithoutError()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            revokedAt: apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_KeyBelongsToOtherOrganization_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (otherUser, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(otherOrg.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_UserLacksApiKeyRevokePermission_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
