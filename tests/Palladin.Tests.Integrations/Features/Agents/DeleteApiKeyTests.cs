using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class DeleteApiKeyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_KeyExists_Then_HardDeletesKeyAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}/permanent");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.ApiKeys.FirstOrDefaultAsync(x => x.Id == apiKey.Id);
        persisted.ShouldBeNull();
    }

    [Fact]
    public async Task When_KeyDoesNotExist_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{Guid.NewGuid()}/permanent");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_KeyBelongsToOtherOrganization_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(otherOrg.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}/permanent");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.ApiKeys.FirstOrDefaultAsync(x => x.Id == apiKey.Id);
        persisted.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_UserLacksApiKeyWritePermission_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.DeleteAsync($"api/api-keys/{apiKey.Id}/permanent");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
