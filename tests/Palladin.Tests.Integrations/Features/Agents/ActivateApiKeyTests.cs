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
public sealed class ActivateApiKeyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_KeyIsRevoked_Then_ActivatesKeyAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            revokedAt: apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.PostAsync($"api/api-keys/{apiKey.Id}/activate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.ApiKeys.FirstOrDefaultAsync(x => x.Id == apiKey.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(ApiKeyStatus.Active);
        persisted.RevokedBy.ShouldBeNull();
        persisted.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_KeyIsAlreadyActive_Then_Returns204WithoutError()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.PostAsync($"api/api-keys/{apiKey.Id}/activate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_KeyBelongsToOtherOrganization_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            otherOrg.Id,
            revokedAt: apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.PostAsync($"api/api-keys/{apiKey.Id}/activate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_UserLacksApiKeyWritePermission_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            revokedAt: apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.PostAsync($"api/api-keys/{apiKey.Id}/activate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
