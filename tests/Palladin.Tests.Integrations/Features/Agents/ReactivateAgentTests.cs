using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class ReactivateAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentIsDeactivated_Then_ReactivatesAgentAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var deactivator = await apiFactory.Services.SeedAgentsUserAsync(Guid.NewGuid());
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Deactivated)
                .RuleFor(x => x.DeactivatedBy, deactivator.Id)
                .RuleFor(x => x.DeactivatedAt, apiFactory.FakeClock.GetCurrentInstant()));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/reactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(AgentStatus.Active);
        persisted.DeactivatedBy.ShouldBeNull();
        persisted.DeactivatedAt.ShouldBeNull();
        persisted.ReactivatedBy.ShouldBe(user.Id);
        persisted.ReactivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_AgentIsNotDeactivated_Then_Returns400()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/reactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
        var response = await client.PostAsync($"api/agents/{agent.Id}/reactivate", null);

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
        var response = await client.PostAsync($"api/agents/{agent.Id}/reactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
