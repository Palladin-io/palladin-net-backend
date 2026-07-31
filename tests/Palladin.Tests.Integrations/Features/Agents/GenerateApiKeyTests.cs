using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class GenerateApiKeyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserHasApiKeyCreatePermission_Then_ReturnsPlaintextOnce()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);

        // When
        var (response, result) = await client
            .POSTAsync<GenerateApiKeyEndpoint, GenerateApiKeyRequest, GenerateApiKeyResponse>(
                new GenerateApiKeyRequest { Name = "CI Pipeline" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Name.ShouldBe("CI Pipeline");
        result.Plaintext.ShouldNotBeNullOrWhiteSpace();
        result.Plaintext.ShouldStartWith("pl_");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.ApiKeys.FirstOrDefaultAsync(x => x.Id == result.ApiKeyId);
        persisted.ShouldNotBeNull();
        persisted.OrganizationId.ShouldBe(organization.Id);
        persisted.CreatedBy.ShouldBe(user.Id);
        persisted.KeyHash.ShouldBe(ApiKey.HashKey(result.Plaintext));
    }

    [Fact]
    public async Task When_UserLacksApiKeyCreatePermission_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, _) = await client
            .POSTAsync<GenerateApiKeyEndpoint, GenerateApiKeyRequest, GenerateApiKeyResponse>(
                new GenerateApiKeyRequest { Name = "CI Pipeline" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
