using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

[Collection<ApiFactoryCollection>]
public sealed class SubmissionDecisionTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ApproverApprovesNewSubmissionWithGrant_Then_EntryGrantAndStateCommitTogether()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var request = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: true);

        // When
        var (response, result) = await scenario.Owner.Client
            .POSTAsync<ApproveSubmissionAsNewEntryEndpoint, ApproveSubmissionAsNewEntryRequest, ApproveSubmissionResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.EntryId.ShouldBe(request.Entry.EntryId);
        result.EntryRevision.ShouldBe("1");
        result.GrantId.ShouldBe(request.Grant!.GrantId);
        var stored = await scenario.LoadSubmissionAsync(submissionId);
        stored.State.ShouldBe(AgentSubmissionState.Approved);
        stored.Document.ShouldBeNull();
        stored.DecidedBy.ShouldBe(scenario.Owner.User.Id);
        stored.DecisionEntryId.ShouldBe(request.Entry.EntryId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Entries.AnyAsync(x => x.Id == request.Entry.EntryId, TestContext.Current.CancellationToken)).ShouldBeTrue();
        var grant = await db.Grants.SingleAsync(x => x.Id == request.Grant.GrantId, TestContext.Current.CancellationToken);
        grant.AgentId.ShouldBe(agent.Id);
        grant.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task When_ApprovalIsRetriedIdentically_Then_SameResultIsReturnedWithoutSecondEntry()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var request = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false);
        await scenario.Owner.Client
            .POSTAsync<ApproveSubmissionAsNewEntryEndpoint, ApproveSubmissionAsNewEntryRequest, ApproveSubmissionResponse>(request);

        // When
        var (retry, result) = await scenario.Owner.Client
            .POSTAsync<ApproveSubmissionAsNewEntryEndpoint, ApproveSubmissionAsNewEntryRequest, ApproveSubmissionResponse>(request);

        // Then
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.EntryId.ShouldBe(request.Entry.EntryId);
        result.GrantId.ShouldBeNull();
    }

    [Fact]
    public async Task When_GrantStagingFails_Then_EntryAndApprovalAreRolledBack()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var request = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: true);
        var foreignAgent = await scenario.CreateAgentAsync();
        request = request with { Grant = request.Grant! with { AgentId = foreignAgent.Id } };

        // When
        var response = await PostApproveNewAsync(scenario.Owner.Client, request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("grant-target-mismatch");
        (await scenario.LoadSubmissionAsync(submissionId)).State.ShouldBe(AgentSubmissionState.Pending);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Entries.AnyAsync(x => x.Id == request.Entry.EntryId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_TwoApproversDecideConcurrently_Then_OneWinsAndTheOtherGetsConflict()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var second = await scenario.AddApproverMemberAsync();
        (await scenario.AddApproversAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await scenario.AddVaultMemberAsync(second.User.Id);
        var ownerRequest = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false);
        var secondRequest = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false, member: second);

        // When
        var responses = await Task.WhenAll(
            PostApproveNewAsync(scenario.Owner.Client, ownerRequest),
            PostApproveNewAsync(second.Client, secondRequest));

        // Then
        responses.Count(x => x.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(x => x.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(loser)).ShouldBe("submission-already-decided");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Entries.CountAsync(x => x.Id == ownerRequest.Entry.EntryId || x.Id == secondRequest.Entry.EntryId,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ApproverApprovesUpdate_Then_NextRevisionGrantRefreshAndStateCommitTogether()
    {
        // Given
        var (scenario, agent, submissionId, entryId, grantId) = await ArrangeUpdateSubmissionAsync();
        var request = UpdateRequest(scenario, agent, submissionId, entryId, grantId, baseRevision: 1);

        // When
        var (response, result) = await scenario.Owner.Client
            .POSTAsync<ApproveSubmissionAsUpdateEndpoint, ApproveSubmissionAsUpdateRequest, ApproveSubmissionResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.EntryRevision.ShouldBe("2");
        var stored = await scenario.LoadSubmissionAsync(submissionId);
        stored.State.ShouldBe(AgentSubmissionState.Approved);
        stored.Document.ShouldBeNull();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.EntryVersions.CountAsync(x => x.EntryId == entryId, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await db.GrantEntryEnvelopes.SingleAsync(x => x.GrantId == grantId, TestContext.Current.CancellationToken))
            .EntryRevision.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_TargetEntryMovedPastExpectedRevision_Then_ApprovalIsStaleAndNothingChanges()
    {
        // Given
        var (scenario, agent, submissionId, entryId, grantId) = await ArrangeUpdateSubmissionAsync();
        var concurrentUpdate = UpdateRequest(scenario, agent, submissionId, entryId, grantId, baseRevision: 1).Entry;
        (await scenario.Owner.Client.PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(concurrentUpdate))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stale = UpdateRequest(scenario, agent, submissionId, entryId, grantId, baseRevision: 2, seed: 90);

        // When
        var response = await scenario.Owner.Client.PostAsJsonAsync(
            $"api/submissions/{submissionId}/approve-update", stale, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("stale-target-revision");
        (await scenario.LoadSubmissionAsync(submissionId)).State.ShouldBe(AgentSubmissionState.Pending);
    }

    [Fact]
    public async Task When_ApproverRedirectsUpdateToNewEntry_Then_NewEntryIsCreatedAndTargetUntouched()
    {
        // Given
        var (scenario, agent, submissionId, entryId, _) = await ArrangeUpdateSubmissionAsync();
        var request = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false);

        // When
        var response = await PostApproveNewAsync(scenario.Owner.Client, request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.EntryVersions.CountAsync(x => x.EntryId == entryId, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_NewSubmissionIsApprovedAsUpdate_Then_ItIsRejected()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var entryId = await scenario.CreateEntryAsync();
        var request = UpdateRequest(scenario, agent, submissionId, entryId, grantId: null, baseRevision: 1);

        // When
        var response = await scenario.Owner.Client.PostAsJsonAsync(
            $"api/submissions/{submissionId}/approve-update", request, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("not-an-update");
    }

    [Fact]
    public async Task When_SubmissionIsDeniedRestoredAndApproved_Then_LifecycleAndAgentUseFollowTheContract()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();

        // When
        var deny = await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/deny");
        var agentAfterDeny = await agent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var (_, trashed) = await scenario.Owner.Client
            .GETAsync<ListSubmissionsEndpoint, ListSubmissionsRequest, ListSubmissionsResponse>(
                new ListSubmissionsRequest { State = "trashed" });
        var denyAgain = await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/deny");
        var restore = await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/restore");
        var agentAfterRestore = await agent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var approve = await PostApproveNewAsync(scenario.Owner.Client,
            await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false));

        // Then
        deny.StatusCode.ShouldBe(HttpStatusCode.OK);
        agentAfterDeny.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        trashed!.Items.ShouldContain(x => x.SubmissionId == submissionId && x.AgentUseSuspended);
        denyAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        restore.StatusCode.ShouldBe(HttpStatusCode.OK);
        agentAfterRestore.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await scenario.LoadSubmissionAsync(submissionId);
        stored.State.ShouldBe(AgentSubmissionState.Approved);
        stored.AgentUseSuspended.ShouldBeTrue();
    }

    [Fact]
    public async Task When_TrashIsOlderThanThirtyDays_Then_PurgeDeletesCiphertextButKeepsStructure()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var (_, _, recentId) = await ArrangeNewSubmissionAsync(scenario);
        (await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/deny")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var start = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Reset(start + Duration.FromDays(29));
        (await PostAsync(scenario.Owner.Client, $"api/submissions/{recentId}/deny")).StatusCode.ShouldBe(HttpStatusCode.OK);
        apiFactory.FakeClock.Reset(start + Duration.FromDays(30) + Duration.FromMinutes(1));
        try
        {
            // When
            await using var scope = apiFactory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PurgeTrashedSubmissionsJob>()
                .ExecuteAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            apiFactory.FakeClock.Reset(start);
        }

        // Then
        var purged = await scenario.LoadSubmissionAsync(submissionId);
        purged.State.ShouldBe(AgentSubmissionState.Purged);
        purged.Document.ShouldBeNull();
        (await scenario.LoadSubmissionAsync(recentId)).State.ShouldBe(AgentSubmissionState.Trashed);
        var restore = await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/restore");
        restore.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var list = await agent.Client.GetFromJsonAsync<JsonElement>("api/agent/submissions", TestContext.Current.CancellationToken);
        list.GetProperty("items").EnumerateArray()
            .ShouldNotContain(x => x.GetProperty("submissionId").GetGuid() == submissionId);
    }

    [Fact]
    public async Task When_RemovedApproverTriesToDecide_Then_DecisionIsRefused()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var removed = await scenario.AddApproverMemberAsync();
        (await scenario.AddApproversAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await scenario.SetApproverAsync(removed.User.Id, false);

        // When
        var deny = await PostAsync(removed.Client, $"api/submissions/{submissionId}/deny");
        var get = await removed.Client.GetAsync($"api/submissions/{submissionId}", TestContext.Current.CancellationToken);

        // Then
        deny.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(deny)).ShouldBe("approver-not-current");
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await scenario.LoadSubmissionAsync(submissionId)).State.ShouldBe(AgentSubmissionState.Pending);
    }

    [Fact]
    public async Task When_SubmissionWasEncryptedToRotatedKey_Then_ApprovalWaitsForAgentRewrap()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var response = await PostApproveNewAsync(scenario.Owner.Client,
            await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-key-rotated");
    }

    [Fact]
    public async Task When_ApproverReviewsSubmission_Then_SignedObjectAndAuthoritativeAgentKeyAreReturned()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent);
        var submissionId = await scenario.SubmitAsync(agent, submission);

        // When
        var (response, result) = await scenario.Owner.Client
            .GETAsync<GetSubmissionEndpoint, SubmissionRouteRequest, GetSubmissionResponse>(
                new SubmissionRouteRequest { SubmissionId = submissionId });
        var (listResponse, list) = await scenario.Owner.Client
            .GETAsync<ListSubmissionsEndpoint, ListSubmissionsRequest, ListSubmissionsResponse>(new ListSubmissionsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Submission.ShouldBe(submission);
        result.AgentEd25519PublicKey.ShouldBe(AgentSubmissionScenario.Encode(Convert.FromBase64String(agent.Signing.PublicKeyBase64)));
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = list!.Items.Single(x => x.SubmissionId == submissionId);
        item.AgentId.ShouldBe(agent.Id);
        item.AgentName.ShouldNotBeNullOrEmpty();
        item.State.ShouldBe("pending");
    }

    [Fact]
    public async Task When_SubmissionLifecycleIsAuditedAndNotified_Then_NoSecretMaterialLeaves()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await apiFactory.Services.SeedNotificationUserAsync(scenario.OrganizationId, scenario.Owner.User.Id, (Permission)int.MaxValue);
        await apiFactory.Services.SeedUserScopeAsync(scenario.OrganizationId, scenario.Owner.User.Id);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent);
        var submissionId = await scenario.SubmitAsync(agent, submission);
        (await PostAsync(scenario.Owner.Client, $"api/submissions/{submissionId}/deny")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var secrets = new[]
        {
            submission.EncodedPayload, submission.EncodedAgentWrappedDek, submission.EncodedApproverWrappedDek,
            submission.AgentSignature,
        };

        // When
        var inbox = await WaitForAsync(async () =>
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
            return await db.InboxItems.Where(x => x.OrganizationId == scenario.OrganizationId && x.SubjectId == submissionId)
                .ToListAsync(TestContext.Current.CancellationToken);
        }, items => items.Count > 0);
        var audit = await WaitForAsync(async () =>
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
            return await db.AuditLogEntries.Where(x => x.OrganizationId == scenario.OrganizationId && x.AgentId == agent.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
        }, entries => entries.Count >= 2);

        // Then
        inbox.ShouldAllBe(x => x.Metadata.Keys.SequenceEqual(new[] { "submissionId" }));
        var submitted = audit.Single(x => x.EventType == "agent-submission.submitted");
        submitted.ActorType.ShouldBe(Palladin.Module.Audit.Contracts.ValueObjects.AuditActorType.Agent);
        submitted.UserId.ShouldBeNull();
        var denied = audit.Single(x => x.EventType == "agent-submission.denied");
        denied.ActorType.ShouldBe(Palladin.Module.Audit.Contracts.ValueObjects.AuditActorType.User);
        denied.UserId.ShouldBe(scenario.Owner.User.Id);
        denied.AgentId.ShouldBe(agent.Id);
        var serialized = JsonSerializer.Serialize(audit.Select(x => new { x.Metadata, x.AgentName, x.ActorName }))
                         + JsonSerializer.Serialize(inbox.Select(x => new { x.Metadata, x.TitleKey }));
        secrets.ShouldAllBe(secret => !serialized.Contains(secret));
    }

    [Theory]
    [InlineData("GET", "api/submissions")]
    [InlineData("GET", "api/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d")]
    [InlineData("POST", "api/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d/approve-new")]
    [InlineData("POST", "api/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d/approve-update")]
    [InlineData("POST", "api/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d/deny")]
    [InlineData("POST", "api/submissions/0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d/restore")]
    public async Task When_CallerLacksGrantManageOrIsAnAgent_Then_DecisionEndpointsAreClosed(string method, string path)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        var withoutGrantManage = apiFactory.CreateAuthenticatedClient(
            scenario.Owner.User, (Permission)int.MaxValue & ~Permission.GrantManage);

        // When
        var forbidden = await withoutGrantManage.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { }),
        }, TestContext.Current.CancellationToken);
        var anonymous = await apiFactory.CreateClient().SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);
        var asAgent = await agent.Client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        // Then
        forbidden.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        if (method == "GET")
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asAgent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_ApproverLacksVaultMembership_Then_ApproveNewIsForbidden()
    {
        // Given
        var (scenario, agent, submissionId) = await ArrangeNewSubmissionAsync();
        var outsider = await scenario.AddApproverMemberAsync();
        (await scenario.AddApproversAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var request = await ApproveNewRequestAsync(scenario, agent, submissionId, withGrant: false);

        // When
        var response = await PostApproveNewAsync(outsider.Client, request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("vault-access-required");
    }

    private async Task<(AgentSubmissionScenario Scenario, ScenarioAgent Agent, Guid SubmissionId)> ArrangeNewSubmissionAsync(
        AgentSubmissionScenario? existing = null)
    {
        var scenario = existing ?? await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        if (existing is null)
        {
            await scenario.BootstrapAsync();
        }

        var submissionId = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        return (scenario, agent, submissionId);
    }

    private async Task<(AgentSubmissionScenario, ScenarioAgent, Guid, Guid, Guid)> ArrangeUpdateSubmissionAsync()
    {
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = await scenario.GrantAsync(agent, entryId);
        var submissionId = await scenario.SubmitAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));
        return (scenario, agent, submissionId, entryId, grantId);
    }

    private async Task<ApproveSubmissionAsNewEntryRequest> ApproveNewRequestAsync(
        AgentSubmissionScenario scenario,
        ScenarioAgent agent,
        Guid submissionId,
        bool withGrant,
        ScenarioMember? member = null)
    {
        var entryId = await scenario.ReserveEntryIdAsync((member ?? scenario.Owner).Client);
        return new ApproveSubmissionAsNewEntryRequest
        {
            SubmissionId = submissionId,
            VaultId = scenario.VaultId,
            Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, entryId),
            Grant = withGrant ? scenario.GrantRequest(agent, entryId, Guid.NewGuid()) : null,
        };
    }

    private static ApproveSubmissionAsUpdateRequest UpdateRequest(
        AgentSubmissionScenario scenario,
        ScenarioAgent agent,
        Guid submissionId,
        Guid entryId,
        Guid? grantId,
        ulong baseRevision,
        int seed = 32) => new()
        {
            SubmissionId = submissionId,
            Entry = EntryEnvelopeFaker.CreateUpdateRequest(
                scenario.OrganizationId, scenario.VaultId, entryId, baseRevision, seed: seed) with
            {
                GrantEnvelopes = grantId is null
                    ? []
                    :
                    [
                        GrantEnvelopeTestData.Contract(
                            scenario.OrganizationId, scenario.VaultId, grantId.Value, entryId, agent.X25519PublicKey,
                            entryRevision: baseRevision + 1, envelopeRevision: baseRevision + 1,
                            grantKeyVersion: (uint)baseRevision + 1, agentId: agent.Id, methods: GrantMethods.Inject),
                    ],
            },
        };

    private static Task<HttpResponseMessage> PostApproveNewAsync(HttpClient client, ApproveSubmissionAsNewEntryRequest request) =>
        client.PostAsJsonAsync($"api/submissions/{request.SubmissionId}/approve-new", request, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path) =>
        client.PostAsync(path, null, TestContext.Current.CancellationToken);

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = await read();
            if (ready(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
