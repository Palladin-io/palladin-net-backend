using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class ListApiKeyAgentsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentsUseDifferentKeys_Then_ReturnsOnlyMatchingKeyAgentsNewestFirst()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();

        var (keyA, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Key A");
        var (keyB, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Key B");

        var now = SystemClock.Instance.GetCurrentInstant();
        var older = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyA.Id)
                .RuleFor(x => x.CreatedAt, now.Minus(Duration.FromMinutes(10))));
        var newer = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyA.Id)
                .RuleFor(x => x.CreatedAt, now));

        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: keyB.Id));
        await apiFactory.Services.SeedAgentAsync(
            otherOrganization.Id,
            AgentFaker.Create(organizationId: otherOrganization.Id, lastUsedApiKeyId: keyA.Id));

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (response, result) = await client
            .GETAsync<ListApiKeyAgentsEndpoint, ListApiKeyAgentsRequest, ListApiKeyAgentsResponse>(
                new ListApiKeyAgentsRequest { ApiKeyId = keyA.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Items.Count.ShouldBe(2);
        result.Items.Select(x => x.AgentId).ShouldBe([newer.Id, older.Id]);
        result.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task When_MoreAgentsThanPageSize_Then_CursorPagesThroughAndEndsWithNullCursor()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (key, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, name: "Key");

        var now = SystemClock.Instance.GetCurrentInstant();
        var agents = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var agent = await apiFactory.Services.SeedAgentAsync(
                organization.Id,
                AgentFaker.Create(organizationId: organization.Id, lastUsedApiKeyId: key.Id)
                    .RuleFor(x => x.CreatedAt, now.Minus(Duration.FromMinutes(i))));
            agents.Add(agent.Id);
        }

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var (firstResponse, firstPage) = await client
            .GETAsync<ListApiKeyAgentsEndpoint, ListApiKeyAgentsRequest, ListApiKeyAgentsResponse>(
                new ListApiKeyAgentsRequest { ApiKeyId = key.Id, PageSize = 2 });

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstPage.ShouldNotBeNull();
        firstPage.Items.Select(x => x.AgentId).ShouldBe([agents[0], agents[1]]);
        firstPage.NextCursor.ShouldNotBeNull();

        // When
        var (secondResponse, secondPage) = await client
            .GETAsync<ListApiKeyAgentsEndpoint, ListApiKeyAgentsRequest, ListApiKeyAgentsResponse>(
                new ListApiKeyAgentsRequest { ApiKeyId = key.Id, PageSize = 2, Cursor = firstPage.NextCursor });

        // Then
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondPage.ShouldNotBeNull();
        secondPage.Items.Select(x => x.AgentId).ShouldBe([agents[2]]);
        secondPage.NextCursor.ShouldBeNull();
    }
}
