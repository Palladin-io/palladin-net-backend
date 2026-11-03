using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Types;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

[Collection<ApiFactoryCollection>]
public sealed class SubmissionTargetTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentHoldsActiveGrant_Then_CurrentRevisionIsReturnedWithoutAnySideEffect()
    {
        // Given
        var (scenario, agent, entryId, grantId) = await ArrangeGrantedAsync();
        var before = await SnapshotAsync(scenario, agent.Id, grantId);

        // When
        var response = await agent.Client.GetAsync(TargetPath(scenario, entryId), TestContext.Current.CancellationToken);
        var repeated = await agent.Client.GetAsync(TargetPath(scenario, entryId), TestContext.Current.CancellationToken);
        var submit = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        submit.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        body.Select(x => x.Key).Order().ShouldBe(["entryId", "entryRevision", "vaultId"]);
        body["vaultId"]!.GetValue<Guid>().ShouldBe(scenario.VaultId);
        body["entryId"]!.GetValue<Guid>().ShouldBe(entryId);
        body["entryRevision"]!.GetValue<string>().ShouldBe("1");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await SnapshotAsync(scenario, agent.Id, grantId)).ShouldBe(before);
    }

    [Fact]
    public async Task When_AgentHasNoGrant_Then_UpdateIsNotGrantedAndNoAccessRequestIsCreated()
    {
        // Given
        var (scenario, granted, entryId, grantId) = await ArrangeGrantedAsync();
        var agent = await scenario.CreateAgentAsync();
        var before = await SnapshotAsync(scenario, agent.Id, grantId);

        // When
        var response = await agent.Client.GetAsync(TargetPath(scenario, entryId), TestContext.Current.CancellationToken);
        var unknownEntry = await granted.Client.GetAsync(TargetPath(scenario, Guid.NewGuid()), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("update-not-granted");
        unknownEntry.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(unknownEntry)).ShouldBe("update-not-granted");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await SnapshotAsync(scenario, agent.Id, grantId)).ShouldBe(before);
    }

    [Fact]
    public async Task When_GrantExpiredButIsNotYetSwept_Then_TargetAndUpdateAreNotGranted()
    {
        // Given
        var (scenario, agent, entryId, grantId) = await ArrangeGrantedAsync();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var expired = apiFactory.FakeClock.GetCurrentInstant() - NodaTime.Duration.FromMinutes(1);
            await db.Grants.Where(x => x.Id == grantId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ExpiresAt, expired)
                    .SetProperty(x => x.QueryLimit, (int?)null), TestContext.Current.CancellationToken);
        }

        var before = await SnapshotAsync(scenario, agent.Id, grantId);

        // When
        var target = await agent.Client.GetAsync(TargetPath(scenario, entryId), TestContext.Current.CancellationToken);
        var submit = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        target.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(target)).ShouldBe("update-not-granted");
        submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(submit)).ShouldBe("update-not-granted");
        (await SnapshotAsync(scenario, agent.Id, grantId)).ShouldBe(before);
    }

    [Fact]
    public async Task When_GrantedEntryIsNoLongerActive_Then_UpdateIsNotGranted()
    {
        // Given
        var (scenario, agent, entryId, _) = await ArrangeGrantedAsync();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await db.Entries.Where(x => x.Id == entryId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, EntryState.Archived),
                    TestContext.Current.CancellationToken);
        }

        // When
        var response = await agent.Client.GetAsync(TargetPath(scenario, entryId), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("update-not-granted");
    }

    [Fact]
    public async Task When_CallerIsNotAnAuthenticatedAgent_Then_SubmissionTargetIsClosed()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var path = TargetPath(scenario, Guid.NewGuid());

        // When
        var anonymous = await apiFactory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        var member = await scenario.Owner.Client.GetAsync(path, TestContext.Current.CancellationToken);

        // Then
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<(AgentSubmissionScenario, ScenarioAgent, Guid, Guid)> ArrangeGrantedAsync()
    {
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = Guid.NewGuid();
        var request = scenario.GrantRequest(agent, entryId, grantId) with
        {
            QueryLimit = 3,
            GrantEntry = GrantEnvelopeTestData.Contract(
                scenario.OrganizationId, scenario.VaultId, grantId, entryId, agent.X25519PublicKey,
                remainingUses: 3, agentId: agent.Id, methods: GrantMethods.Inject),
        };
        var (response, _) = await scenario.Owner.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (scenario, agent, entryId, grantId);
    }

    private async Task<string> SnapshotAsync(AgentSubmissionScenario scenario, Guid agentId, Guid grantId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var grants = await vault.Grants
            .Where(x => x.OrganizationId == scenario.OrganizationId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.QueryCount, x.AgentId })
            .ToListAsync(TestContext.Current.CancellationToken);
        var accessEvents = await audit.AuditLogEntries
            .Where(x => x.OrganizationId == scenario.OrganizationId && x.AgentId == agentId
                        && (x.EventType.StartsWith("credential.") || x.EventType == "grant.consumed"
                            || x.EventType == "grant.requested"))
            .CountAsync(TestContext.Current.CancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(new { grants, accessEvents, grantId });
    }

    private static string TargetPath(AgentSubmissionScenario scenario, Guid entryId) =>
        $"api/agent/vaults/{scenario.VaultId}/entries/{entryId}/submission-target";
}
