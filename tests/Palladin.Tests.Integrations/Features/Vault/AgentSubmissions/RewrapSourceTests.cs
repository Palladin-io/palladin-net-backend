using System.Net;
using System.Net.Http.Json;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Contract preApprovalUse.reWrapSource: a suspended pending submission must stay re-wrappable after
// an approver key change, while pre-approval use stays refused.
[Collection<ApiFactoryCollection>]
public sealed class RewrapSourceTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_SuspendedPendingSubmissionFollowsARotation_Then_SourceIsServedAndRewrapSucceedsButUseStaysRefused()
    {
        // Given
        var (scenario, agent, submission) = await ArrangeSuspendedPendingAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var source = await agent.Client.GetAsync(SourcePath(submission), TestContext.Current.CancellationToken);
        var served = await source.Content.ReadFromJsonAsync<AgentEntrySubmissionContract>(TestContext.Current.CancellationToken);
        var rewrap = await AgentSubmissionScenario.PutRewrapAsync(agent, scenario.Rewrap(agent, served!));
        var use = await agent.Client.GetAsync($"api/agent/submissions/{submission.SubmissionId}", TestContext.Current.CancellationToken);

        // Then
        source.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.ShouldBe(submission);
        rewrap.StatusCode.ShouldBe(HttpStatusCode.OK, await rewrap.Content.ReadAsStringAsync());
        use.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(use)).ShouldBe("submission-not-usable");
        var stored = await scenario.LoadSubmissionAsync(Guid.Parse(submission.SubmissionId));
        stored.SubmissionRevision.ShouldBe(2UL);
        stored.AgentUseSuspended.ShouldBeTrue();
        var harness = apiFactory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<AgentSubmissionChangedEvent>(x =>
            x.Context.Message.SubmissionId == stored.Id
            && x.Context.Message.Action == AgentSubmissionAction.RewrapSourceServed,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        harness.Published.Select<AgentSubmissionChangedEvent>(TestContext.Current.CancellationToken)
            .Any(x => x.Context.Message.SubmissionId == stored.Id && x.Context.Message.Action == AgentSubmissionAction.Used)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_ApproverKeyIsUnchanged_Then_RewrapSourceIsNotServed()
    {
        // Given
        var (_, agent, submission) = await ArrangeSuspendedPendingAsync();

        // When
        var response = await agent.Client.GetAsync(SourcePath(submission), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("rewrap-not-required");
    }

    [Fact]
    public async Task When_SubmissionIsTrashedApprovedOrForeign_Then_RewrapSourceIsRefused()
    {
        // Given
        var (scenario, agent, trashed) = await ArrangeSuspendedPendingAsync(restore: false);
        var approved = scenario.NewSubmission(agent);
        var approvedId = await scenario.SubmitAsync(agent, approved);
        var entryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);
        (await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{approvedId}/approve-new",
            new ApproveSubmissionAsNewEntryRequest
            {
                SubmissionId = approvedId,
                VaultId = scenario.VaultId,
                Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, entryId),
            }, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var pending = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, pending);
        var other = await scenario.CreateAgentAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var trashedResponse = await agent.Client.GetAsync(SourcePath(trashed), TestContext.Current.CancellationToken);
        var approvedResponse = await agent.Client.GetAsync(SourcePath(approved), TestContext.Current.CancellationToken);
        var foreignResponse = await other.Client.GetAsync(SourcePath(pending), TestContext.Current.CancellationToken);

        // Then
        trashedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(trashedResponse)).ShouldBe("submission-not-rewrappable");
        approvedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreignResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AgentSubmissionScenario.ErrorCodeAsync(foreignResponse)).ShouldBe("submission-not-found");
    }

    [Fact]
    public async Task When_AgentAccessEpochChanged_Then_RewrapSourceIsRefused()
    {
        // Given
        var (scenario, agent, submission) = await ArrangeSuspendedPendingAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var agents = scope.ServiceProvider.GetRequiredService<Palladin.Module.Agents.Infrastructure.Persistence.AgentsDbWriteContext>();
            await agents.Agents.Where(x => x.Id == agent.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AccessEpoch, 2u), TestContext.Current.CancellationToken);
            var vault = scope.ServiceProvider.GetRequiredService<Palladin.Module.Vault.Infrastructure.Persistence.VaultDbWriteContext>();
            await vault.Agents.Where(x => x.Id == agent.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AccessEpoch, 2u), TestContext.Current.CancellationToken);
        }

        // When
        var response = await agent.Client.GetAsync(SourcePath(submission), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("submission-not-rewrappable");
    }

    [Fact]
    public async Task When_CallerIsNotAnAuthenticatedAgent_Then_RewrapSourceIsClosed()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var path = $"api/agent/submissions/{Guid.NewGuid()}/rewrap-source";

        // When
        var anonymous = await apiFactory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        var member = await scenario.Owner.Client.GetAsync(path, TestContext.Current.CancellationToken);

        // Then
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<(AgentSubmissionScenario, ScenarioAgent, AgentEntrySubmissionContract)> ArrangeSuspendedPendingAsync(
        bool restore = true)
    {
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent);
        var submissionId = await scenario.SubmitAsync(agent, submission);
        (await scenario.Owner.Client.PostAsync($"api/submissions/{submissionId}/deny", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        if (restore)
        {
            (await scenario.Owner.Client.PostAsync($"api/submissions/{submissionId}/restore", null, TestContext.Current.CancellationToken))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return (scenario, agent, submission);
    }

    private static string SourcePath(AgentEntrySubmissionContract submission) =>
        $"api/agent/submissions/{submission.SubmissionId}/rewrap-source";
}
