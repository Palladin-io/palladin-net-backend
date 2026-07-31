using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
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
public sealed class ListAgentsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserHasAgentManagePermission_Then_ReturnsOnlyOrgAgents()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();

        var ownAgent = await apiFactory.Services.SeedAgentAsync(organization.Id);
        await apiFactory.Services.SeedAgentAsync(otherOrganization.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, result) = await client
            .GETAsync<ListAgentsEndpoint, ListAgentsResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].AgentId.ShouldBe(ownAgent.Id);
        // The list omits the full public key — only the detail endpoint returns it.
        result.Items[0].PublicKey.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentsHaveDifferentStatuses_Then_ReturnsAllStatuses()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();

        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Deactivated));

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, result) = await client
            .GETAsync<ListAgentsEndpoint, ListAgentsResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(3);
        result.Items.Select(x => x.Status).ShouldBe(
            [AgentStatus.Pending, AgentStatus.Active, AgentStatus.Deactivated],
            ignoreOrder: true);
    }

    [Fact]
    public async Task When_AgentHasEnrollerAndDeactivator_Then_ReturnsResolvedNames()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var enroller = await apiFactory.Services.SeedAgentsUserAsync(Guid.NewGuid(), displayName: "Ada Lovelace");
        var deactivator = await apiFactory.Services.SeedAgentsUserAsync(Guid.NewGuid(), displayName: "Grace Hopper");

        var publicKey = AgentFaker.GeneratePublicKey();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Deactivated)
                .RuleFor(x => x.EnrolledBy, enroller.Id)
                .RuleFor(x => x.DeactivatedBy, deactivator.Id));

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, result) = await client
            .GETAsync<ListAgentsEndpoint, ListAgentsResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].AgentId.ShouldBe(agent.Id);
        result.Items[0].EnrolledByName.ShouldBe("Ada Lovelace");
        result.Items[0].DeactivatedByName.ShouldBe("Grace Hopper");
        result.Items[0].PublicKeySuffix.ShouldBe(publicKey[^8..]);
    }

    [Fact]
    public async Task When_UserLacksAgentManagePermission_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, _) = await client
            .GETAsync<ListAgentsEndpoint, ListAgentsResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
