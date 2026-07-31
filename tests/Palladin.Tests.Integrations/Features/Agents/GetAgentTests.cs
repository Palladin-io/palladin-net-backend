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
public sealed class GetAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentExists_Then_ReturnsAgent()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var publicKey = AgentFaker.GeneratePublicKey();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, result) = await client
            .GETAsync<GetAgentEndpoint, GetAgentRequest, AgentSummary>(
                new GetAgentRequest { AgentId = agent.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.AgentId.ShouldBe(agent.Id);
        result.Status.ShouldBe(AgentStatus.Active);
        result.PublicKeySuffix.ShouldBe(publicKey[^8..]);
        // Detail returns the full public key for sealing a DEK on proactive grant.
        result.PublicKey.ShouldBe(publicKey);
    }

    [Fact]
    public async Task When_AgentDoesNotExist_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, _) = await client
            .GETAsync<GetAgentEndpoint, GetAgentRequest, AgentSummary>(
                new GetAgentRequest { AgentId = Guid.NewGuid() });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_AgentBelongsToOtherOrganization_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(otherOrg.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var (response, _) = await client
            .GETAsync<GetAgentEndpoint, GetAgentRequest, AgentSummary>(
                new GetAgentRequest { AgentId = agent.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_UserLacksAgentManagePermission_Then_Returns403()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(organization.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, _) = await client
            .GETAsync<GetAgentEndpoint, GetAgentRequest, AgentSummary>(
                new GetAgentRequest { AgentId = agent.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
