using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class DeleteAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentIsDeactivated_Then_DeletesAgentAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Deactivated));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.DeleteAsync($"api/agents/{agent.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldBeNull();
    }

    [Fact]
    public async Task When_DeletedAgentHasHiddenCredential_Then_EvictsItsAuthenticationCache()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Deactivated));
        var credential = ApiKeyCredential.FromPlaintext(
            Guid.NewGuid(),
            apiKey.Id,
            agent.Id,
            "pl_test-hidden-credential"u8,
            apiFactory.FakeClock.GetCurrentInstant());
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<AgentsDbWriteContext>();
            writeContext.ApiKeyCredentials.Add(credential);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var cache = apiFactory.Services.GetRequiredService<IMemoryCache>();
        cache.Set(
            AgentAuthenticationHandler.ApiKeyCacheKey(credential.KeyHash),
            new ResolvedApiKey(apiKey.Id, organization.Id, agent.Id));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        var response = await client.DeleteAsync(
            $"api/agents/{agent.Id}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        cache.TryGetValue(
            AgentAuthenticationHandler.ApiKeyCacheKey(credential.KeyHash),
            out ResolvedApiKey? _).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentIsActive_Then_Returns409()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.DeleteAsync($"api/agents/{agent.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_AgentIsPending_Then_Returns409()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.DeleteAsync($"api/agents/{agent.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_AgentDoesNotExist_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.DeleteAsync($"api/agents/{Guid.NewGuid()}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_AgentBelongsToOtherOrganization_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            otherOrg.Id,
            AgentFaker.Create(organizationId: otherOrg.Id).RuleFor(x => x.Status, AgentStatus.Deactivated));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.DeleteAsync($"api/agents/{agent.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_UserLacksAgentManagePermission_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Deactivated));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.DeleteAsync($"api/agents/{agent.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
