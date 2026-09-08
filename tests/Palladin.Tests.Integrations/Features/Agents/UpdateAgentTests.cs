using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Agents.Domain;
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
public sealed class UpdateAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_NameAndDescriptionProvided_Then_UpdatesBothAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Name, "Old Name"));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Name = "New Name", Description = "Build pipeline agent" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(
            x => x.Id == agent.Id,
            TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("New Name");
        persisted.Description.ShouldBe("Build pipeline agent");
    }

    [Fact]
    public async Task When_OnlyDescriptionProvided_Then_LeavesNameUnchanged()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Name, "Keep Me"));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Description = "Only description" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(
            x => x.Id == agent.Id,
            TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("Keep Me");
        persisted.Description.ShouldBe("Only description");
    }

    [Fact]
    public async Task When_CustomTypeProvidedOrCleared_Then_PreservesFreeFormContract()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When — a custom value is normalized and stored without catalog mapping.
        var customResponse = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Type = "  custom-runtime  " });

        // Then
        customResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            var persisted = await readContext.Agents.SingleAsync(
                x => x.Id == agent.Id,
                TestContext.Current.CancellationToken);
            persisted.Type.ShouldBe("custom-runtime");
        }

        // And — an explicitly blank value clears the optional metadata.
        var clearResponse = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Type = "   " });
        clearResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            var persisted = await readContext.Agents.SingleAsync(
                x => x.Id == agent.Id,
                TestContext.Current.CancellationToken);
            persisted.Type.ShouldBeNull();
        }
    }

    [Fact]
    public async Task When_AgentDoesNotExist_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = Guid.NewGuid(), Name = "New Name" });

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
        var response = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Name = "New Name" });

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
        var response = await client.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Name = "New Name" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
