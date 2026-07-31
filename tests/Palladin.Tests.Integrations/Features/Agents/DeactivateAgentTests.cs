using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Palladin.Tests.Integrations.Shared.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class DeactivateAgentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentIsActive_Then_StartsStagedDeactivationAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var discovery = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agent.Id);
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agent.Id,
            publicKey: discovery.X25519PublicKey,
            signingPublicKey: discovery.RequestSigning.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(discovery.Request, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Agents.FirstOrDefaultAsync(x => x.Id == agent.Id);
        persisted.ShouldNotBeNull();
        persisted.Status.ShouldBe(AgentStatus.Deactivating);
        persisted.DeactivatedBy.ShouldBe(user.Id);
        persisted.DeactivationRequestId.ShouldNotBeNull();
        persisted.DeactivatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_PendingAgentHasNoVaults_Then_DeactivationCompletesAndReturns204()
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
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var persisted = await WaitForStatusAsync(agent.Id, AgentStatus.Deactivated);
        persisted.DeactivatedBy.ShouldBe(user.Id);
        persisted.DeactivationRequestId.ShouldNotBeNull();
        persisted.DeactivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_AllVaultRotationsComplete_Then_AgentBecomesDeactivated()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);
        await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);
        Guid requestId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            requestId = (await scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>()
                .Agents.SingleAsync(x => x.Id == agent.Id)).DeactivationRequestId!.Value;
        }

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = ActivatorUtilities.CreateInstance<OnAgentDeactivationCompleted>(scope.ServiceProvider);
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentDeactivationCompletedEvent(
                requestId,
                organization.Id,
                agent.Id,
                apiFactory.FakeClock.GetCurrentInstant(),
                apiFactory.FakeClock.GetCurrentInstant())));
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>()
            .Agents.SingleAsync(x => x.Id == agent.Id);
        persisted.Status.ShouldBe(AgentStatus.Deactivated);
        persisted.DeactivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_StaleCompletionArrivesAfterReactivation_Then_ItIsIgnored()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var completedAt = apiFactory.FakeClock.GetCurrentInstant();
        var reactivatedAt = completedAt + NodaTime.Duration.FromMinutes(1);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id)
                .RuleFor(x => x.Status, AgentStatus.Active)
                .RuleFor(x => x.UpdatedAt, reactivatedAt)
                .RuleFor(x => x.ReactivatedAt, reactivatedAt));

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = ActivatorUtilities.CreateInstance<OnAgentDeactivationCompleted>(scope.ServiceProvider);
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentDeactivationCompletedEvent(
                Guid.NewGuid(),
                organization.Id,
                agent.Id,
                completedAt,
                completedAt)));
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>()
            .Agents.SingleAsync(x => x.Id == agent.Id);
        persisted.Status.ShouldBe(AgentStatus.Active);
        persisted.UpdatedAt.ShouldBeGreaterThan(completedAt);
    }

    [Fact]
    public async Task When_AgentIsAlreadyDeactivated_Then_Returns400()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Deactivated));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);

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
            AgentFaker.Create(organizationId: otherOrg.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);

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
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Status, AgentStatus.Active));
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<Agent> WaitForStatusAsync(Guid agentId, AgentStatus expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var agent = await scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>()
                .Agents.SingleAsync(x => x.Id == agentId);
            if (agent.Status == expected)
            {
                return agent;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Agent {agentId} did not reach {expected}.");
    }
}
