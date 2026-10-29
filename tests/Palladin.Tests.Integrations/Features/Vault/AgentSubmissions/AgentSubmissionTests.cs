using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Types;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

[Collection<ApiFactoryCollection>]
public sealed class AgentSubmissionTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentSubmitsNewEntry_Then_PendingSubmissionIsStoredOnce()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var submission = scenario.NewSubmission(agent);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, submission);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<AgentSubmissionStateResponse>(TestContext.Current.CancellationToken);
        body.ShouldBe(new AgentSubmissionStateResponse(Guid.Parse(submission.SubmissionId), "pending", "1"));
        var stored = await scenario.LoadSubmissionAsync(Guid.Parse(submission.SubmissionId));
        stored.State.ShouldBe(AgentSubmissionState.Pending);
        stored.AgentId.ShouldBe(agent.Id);
        stored.Document.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_IdenticalSubmissionIsRetried_Then_SameStateIsReturnedAndConflictingBytesAreRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var submission = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, submission);
        var conflicting = scenario.NewSubmission(agent, Guid.Parse(submission.SubmissionId), payloadSeed: 99);

        // When
        var retry = await AgentSubmissionScenario.PostSubmissionAsync(agent, submission);
        var conflict = await AgentSubmissionScenario.PostSubmissionAsync(agent, conflicting);

        // Then
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(conflict)).ShouldBe("submission-conflict");
    }

    [Fact]
    public async Task When_SubmissionClaimsAnotherAgentOrOrganization_Then_ContextMismatchIsRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var other = await scenario.CreateAgentAsync();
        var foreignAgent = AgentSubmissionScenario.Sign(agent, scenario.NewSubmission(agent) with { AgentId = other.Id.ToString("D") });
        var foreignOrganization = AgentSubmissionScenario.Sign(agent,
            scenario.NewSubmission(agent) with { OrganizationId = Guid.NewGuid().ToString("D") });

        // When
        var agentResponse = await AgentSubmissionScenario.PostSubmissionAsync(agent, foreignAgent);
        var organizationResponse = await AgentSubmissionScenario.PostSubmissionAsync(agent, foreignOrganization);

        // Then
        agentResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        organizationResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(agentResponse)).ShouldBe("context-mismatch");
        (await AgentSubmissionScenario.ErrorCodeAsync(organizationResponse)).ShouldBe("context-mismatch");
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("keyVersion")]
    [InlineData("fingerprint")]
    public async Task When_AgentKeyBindingIsStale_Then_SubmissionIsRejected(string binding)
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var valid = scenario.NewSubmission(agent);
        var stale = AgentSubmissionScenario.Sign(agent, binding switch
        {
            "epoch" => valid with { AgentAccessEpoch = "2" },
            "keyVersion" => valid with { AgentKeyVersion = 2 },
            _ => valid with { AgentKeyFingerprint = AgentSubmissionScenario.Encode(new byte[32]) },
        });

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, stale);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("agent-key-mismatch");
    }

    [Fact]
    public async Task When_AgentSignatureIsInvalid_Then_SubmissionIsRejectedBeforePersistence()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var signed = scenario.NewSubmission(agent);
        var tampered = signed with { EncodedPayload = scenario.NewSubmission(agent, payloadSeed: 42).EncodedPayload };

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, tampered);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("invalid-agent-signature");
        var list = await agent.Client.GetFromJsonAsync<JsonObject>("api/agent/submissions", TestContext.Current.CancellationToken);
        list!["items"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task When_SubmissionContainsUnknownFieldOrScriptType_Then_ShapeIsRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            scenario.NewSubmission(agent), System.Text.Json.JsonSerializerOptions.Web))!.AsObject();
        node["note"] = "x";
        var script = AgentSubmissionScenario.Sign(agent, scenario.NewSubmission(agent, entryType: 2));

        // When
        var unknown = await agent.Client.PostAsJsonAsync("api/agent/submissions", node, TestContext.Current.CancellationToken);
        var scriptResponse = await AgentSubmissionScenario.PostSubmissionAsync(agent, script);

        // Then
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        scriptResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(scriptResponse)).ShouldBe("unsupported-entry-type");
    }

    [Fact]
    public async Task When_NoApproverKeySetExists_Then_SubmissionFailsWithNoApprovers()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent);
        await scenario.SetApproverAsync(scenario.Owner.User.Id, false);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, submission);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("no-approvers");
    }

    [Fact]
    public async Task When_ApproverKeyRotatedBeforeSubmission_Then_StaleKeyIsRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var stale = scenario.NewSubmission(agent);
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, stale);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-key-rotated");
    }

    [Fact]
    public async Task When_RemovedApproverStillHoldsCurrentKey_Then_NewSubmissionsWaitForRotation()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var removed = await scenario.AddApproverMemberAsync();
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        await scenario.SetApproverAsync(removed.User.Id, false);

        // When
        var beforeRotation = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.NewSubmission(agent));
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterRotation = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.NewSubmission(agent));

        // Then
        beforeRotation.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(beforeRotation)).ShouldBe("approver-key-unavailable");
        afterRotation.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task When_UpdateTargetsEntryWithoutActiveGrant_Then_UpdateIsNotGranted()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var entryId = await scenario.CreateEntryAsync();
        var other = await scenario.CreateAgentAsync();
        await scenario.GrantAsync(other, entryId);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("update-not-granted");
    }

    [Fact]
    public async Task When_UpdateTargetsGrantedEntry_Then_SubmissionIsAccepted()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var entryId = await scenario.CreateEntryAsync();
        await scenario.GrantAsync(agent, entryId);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task When_AgentRewrapsAfterRotation_Then_NextRevisionIsAcceptedAndReplaysAreRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var original = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, original);
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rewrapped = scenario.Rewrap(agent, original);

        // When
        var accepted = await AgentSubmissionScenario.PutRewrapAsync(agent, rewrapped);
        var exactRetry = await AgentSubmissionScenario.PutRewrapAsync(agent, rewrapped);
        var replay = await AgentSubmissionScenario.PutRewrapAsync(agent, original);
        var skipped = await AgentSubmissionScenario.PutRewrapAsync(agent,
            AgentSubmissionScenario.Sign(agent, scenario.Rewrap(agent, rewrapped) with { SubmissionRevision = "4" }));

        // Then
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        exactRetry.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(replay)).ShouldBe("revision-replay");
        skipped.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(skipped)).ShouldBe("revision-replay");
        var stored = await scenario.LoadSubmissionAsync(Guid.Parse(original.SubmissionId));
        stored.SubmissionRevision.ShouldBe(2UL);
        stored.ApproverKeyVersion.ShouldBe(2u);
    }

    [Fact]
    public async Task When_RewrapAdvancesRevisionWithoutApproverKeyChange_Then_ItIsRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var original = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, original);
        var sameKey = scenario.Rewrap(agent, original);

        // When
        var response = await AgentSubmissionScenario.PutRewrapAsync(agent, sameKey);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-key-rotated");
        (await scenario.LoadSubmissionAsync(Guid.Parse(original.SubmissionId))).SubmissionRevision.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_AgentAccessEpochChangedAfterSubmission_Then_PreApprovalUseIsRefused()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var submission = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, submission);
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
        var response = await agent.Client.GetAsync($"api/agent/submissions/{submission.SubmissionId}", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("submission-not-usable");
    }

    [Fact]
    public async Task When_RewrapChangesPayloadOrTargetsOldKey_Then_ItIsRejected()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var original = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, original);
        var oldKey = scenario.Rewrap(agent, original);
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var changedPayload = AgentSubmissionScenario.Sign(agent, scenario.Rewrap(agent, original) with
        {
            EncodedPayload = scenario.NewSubmission(agent, payloadSeed: 77).EncodedPayload,
        });

        // When
        var payloadResponse = await AgentSubmissionScenario.PutRewrapAsync(agent, changedPayload);
        var oldKeyResponse = await AgentSubmissionScenario.PutRewrapAsync(agent, oldKey);

        // Then
        payloadResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(payloadResponse)).ShouldBe("rewrap-changed-payload");
        oldKeyResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(oldKeyResponse)).ShouldBe("approver-key-rotated");
    }

    [Fact]
    public async Task When_AuthorFetchesPendingSubmission_Then_OnlyTheAuthorReceivesIt()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var other = await scenario.CreateAgentAsync();
        var submission = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, submission);

        // When
        var own = await agent.Client.GetAsync($"api/agent/submissions/{submission.SubmissionId}", TestContext.Current.CancellationToken);
        var foreign = await other.Client.GetAsync($"api/agent/submissions/{submission.SubmissionId}", TestContext.Current.CancellationToken);

        // Then
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadFromJsonAsync<AgentEntrySubmissionContract>(TestContext.Current.CancellationToken))
            .ShouldBe(submission);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(foreign)).ShouldBe("submission-not-usable");
    }

    [Fact]
    public async Task When_AgentListsSubmissions_Then_OnlyOwnStructuralItemsWithoutCiphertextAreReturned()
    {
        // Given
        var (scenario, agent) = await ArrangeAsync();
        var other = await scenario.CreateAgentAsync();
        var submission = scenario.NewSubmission(agent);
        await scenario.SubmitAsync(agent, submission);
        await scenario.SubmitAsync(other, scenario.NewSubmission(other));

        // When
        var response = await agent.Client.GetAsync("api/agent/submissions", TestContext.Current.CancellationToken);

        // Then
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = JsonNode.Parse(body)!["items"]!.AsArray();
        items.Count.ShouldBe(1);
        items[0]!["submissionId"]!.GetValue<string>().ShouldBe(submission.SubmissionId);
        items[0]!["state"]!.GetValue<string>().ShouldBe("pending");
        items[0]!["currentApproverKeyVersion"]!.GetValue<uint>().ShouldBe(1u);
        body.ShouldNotContain(submission.EncodedPayload);
        body.ShouldNotContain(submission.EncodedApproverWrappedDek);
    }

    [Theory]
    [InlineData("GET", "api/agent/approver-manifests")]
    [InlineData("POST", "api/agent/submissions")]
    [InlineData("GET", "api/agent/submissions")]
    [InlineData("GET", "api/agent/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d")]
    [InlineData("PUT", "api/agent/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d/wrap")]
    public async Task When_CallerIsNotAnAuthenticatedAgent_Then_AgentSubmissionEndpointsAreClosed(string method, string path)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);

        // When
        var anonymous = await apiFactory.CreateClient().SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);
        var member = await scenario.Owner.Client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        // Then
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        member.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<(AgentSubmissionScenario Scenario, ScenarioAgent Agent)> ArrangeAsync()
    {
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        return (scenario, agent);
    }
}
