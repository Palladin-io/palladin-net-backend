using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using System.Net;
using Palladin.Core.Security;

using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class ApproveAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentIsPending_Then_ActivatesAgentAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.POSTAsync<ApproveAgentEndpoint, ApproveAgentRequest>(
            new ApproveAgentRequest { AgentId = agent.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(AgentStatus.Active);
        persisted.EnrolledBy.ShouldBe(user.Id);
        persisted.EnrolledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_NameTypeAndIconProvided_Then_PersistsThemOnAgent()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.POSTAsync<ApproveAgentEndpoint, ApproveAgentRequest>(
            new ApproveAgentRequest
            {
                AgentId = agent.Id,
                Name = "Build Agent",
                Type = "claudeCode",
                IconKey = "smart_toy",
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(AgentStatus.Active);
        persisted.Name.ShouldBe("Build Agent");
        persisted.Type.ShouldBe("claudeCode");
        persisted.IconKey.ShouldBe("smart_toy");
    }

    [Fact]
    public async Task When_AgentIsNotPending_Then_Returns400()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.POSTAsync<ApproveAgentEndpoint, ApproveAgentRequest>(
            new ApproveAgentRequest { AgentId = agent.Id });

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
            AgentFaker.Create(organizationId: otherOrg.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.POSTAsync<ApproveAgentEndpoint, ApproveAgentRequest>(
            new ApproveAgentRequest { AgentId = agent.Id });

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
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.POSTAsync<ApproveAgentEndpoint, ApproveAgentRequest>(
            new ApproveAgentRequest { AgentId = agent.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
