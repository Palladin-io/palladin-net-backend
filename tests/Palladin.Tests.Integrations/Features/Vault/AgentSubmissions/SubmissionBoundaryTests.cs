using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using MassTransit.Testing;
using NSubstitute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Isolation, authority and leak boundaries of Agent submissions that are independent of the happy path.
[Collection<ApiFactoryCollection>]
public sealed class SubmissionBoundaryTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_SubmissionsArePending_Then_NoVaultEntryGrantOrSequenceChanges()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        await scenario.GrantAsync(agent, entryId);
        var before = await VaultStateAsync(scenario);

        // When
        await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        await scenario.SubmitAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        (await VaultStateAsync(scenario)).ShouldBe(before);
    }

    [Fact]
    public async Task When_ApprovingWithoutGrant_Then_NoGrantIsCreatedAndExistingGrantsKeepTheirIdentity()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = await scenario.GrantAsync(agent, entryId);
        var newSubmission = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        var updateSubmission = await scenario.SubmitAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));
        var newEntryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);

        // When
        var approveNew = await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{newSubmission}/approve-new",
            new ApproveSubmissionAsNewEntryRequest
            {
                SubmissionId = newSubmission,
                VaultId = scenario.VaultId,
                Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, newEntryId),
            }, TestContext.Current.CancellationToken);
        var approveUpdate = await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{updateSubmission}/approve-update",
            new ApproveSubmissionAsUpdateRequest
            {
                SubmissionId = updateSubmission,
                Entry = EntryEnvelopeFaker.CreateUpdateRequest(scenario.OrganizationId, scenario.VaultId, entryId, 1) with
                {
                    GrantEnvelopes =
                    [
                        GrantEnvelopeTestData.Contract(scenario.OrganizationId, scenario.VaultId, grantId, entryId,
                            agent.X25519PublicKey, entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2,
                            agentId: agent.Id, methods: GrantMethods.Inject),
                    ],
                },
            }, TestContext.Current.CancellationToken);

        // Then
        approveNew.StatusCode.ShouldBe(HttpStatusCode.OK);
        approveUpdate.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grants = await db.Grants.Where(x => x.OrganizationId == scenario.OrganizationId)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(TestContext.Current.CancellationToken);
        grants.ShouldHaveSingleItem().Id.ShouldBe(grantId);
        grants.Single().Status.ShouldBe(GrantStatus.Active);
        (await db.GrantEntryScopes.SingleAsync(x => x.GrantId == grantId, TestContext.Current.CancellationToken))
            .EntryId.ShouldBe(entryId);
    }

    [Fact]
    public async Task When_ApproverLacksVaultManageOrTargetMembership_Then_ApprovalIsForbiddenAndNothingChanges()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = await scenario.GrantAsync(agent, entryId);
        var newSubmission = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        var updateSubmission = await scenario.SubmitAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));
        var outsider = await scenario.AddApproverMemberAsync();
        (await scenario.AddApproversAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var withoutVaultManage = apiFactory.CreateAuthenticatedClient(
            scenario.Owner.User, (Permission)int.MaxValue & ~Permission.VaultManage);
        var newEntryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);
        var before = await VaultStateAsync(scenario);

        // When
        var newWithoutPermission = await withoutVaultManage.PostAsJsonAsync($"api/submissions/{newSubmission}/approve-new",
            new ApproveSubmissionAsNewEntryRequest
            {
                SubmissionId = newSubmission,
                VaultId = scenario.VaultId,
                Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, newEntryId),
            }, TestContext.Current.CancellationToken);
        var updateByOutsider = await outsider.Client.PostAsJsonAsync($"api/submissions/{updateSubmission}/approve-update",
            new ApproveSubmissionAsUpdateRequest
            {
                SubmissionId = updateSubmission,
                Entry = EntryEnvelopeFaker.CreateUpdateRequest(scenario.OrganizationId, scenario.VaultId, entryId, 1) with
                {
                    GrantEnvelopes =
                    [
                        GrantEnvelopeTestData.Contract(scenario.OrganizationId, scenario.VaultId, grantId, entryId,
                            agent.X25519PublicKey, entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2,
                            agentId: agent.Id, methods: GrantMethods.Inject),
                    ],
                },
            }, TestContext.Current.CancellationToken);

        // Then
        newWithoutPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(newWithoutPermission)).ShouldBe("vault-access-required");
        updateByOutsider.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(updateByOutsider)).ShouldBe("vault-access-required");
        (await VaultStateAsync(scenario)).ShouldBe(before);
        (await scenario.LoadSubmissionAsync(newSubmission)).State.ShouldBe(AgentSubmissionState.Pending);
        (await scenario.LoadSubmissionAsync(updateSubmission)).State.ShouldBe(AgentSubmissionState.Pending);
    }

    [Fact]
    public async Task When_PrincipalsOfAnotherOrganizationTouchASubmission_Then_TheyNeitherSeeNorDecideIt()
    {
        // Given
        var home = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await home.CreateAgentAsync();
        await home.BootstrapAsync();
        var submission = home.NewSubmission(agent);
        var submissionId = await home.SubmitAsync(agent, submission);
        var foreign = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var foreignAgent = await foreign.CreateAgentAsync();
        await foreign.BootstrapAsync();

        // When
        var memberGet = await foreign.Owner.Client.GetAsync($"api/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var memberDeny = await foreign.Owner.Client.PostAsync($"api/submissions/{submissionId}/deny", null, TestContext.Current.CancellationToken);
        var (_, memberList) = await foreign.Owner.Client
            .GETAsync<ListSubmissionsEndpoint, ListSubmissionsRequest, ListSubmissionsResponse>(new ListSubmissionsRequest());
        var agentGet = await foreignAgent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var agentRewrap = await AgentSubmissionScenario.PutRewrapAsync(foreignAgent,
            AgentSubmissionScenario.Sign(foreignAgent, submission with { SubmissionRevision = "2" }));
        var agentClaimsHomeOrganization = await AgentSubmissionScenario.PostSubmissionAsync(foreignAgent,
            AgentSubmissionScenario.Sign(foreignAgent, foreign.NewSubmission(foreignAgent) with
            {
                OrganizationId = home.OrganizationId.ToString("D"),
            }));

        // Then
        memberGet.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        memberDeny.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        memberList!.Items.ShouldNotContain(x => x.SubmissionId == submissionId);
        agentGet.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        agentRewrap.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(agentRewrap)).ShouldBe("context-mismatch");
        agentClaimsHomeOrganization.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var stored = await home.LoadSubmissionAsync(submissionId);
        stored.State.ShouldBe(AgentSubmissionState.Pending);
        stored.SubmissionRevision.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_UpdateTargetsEntryOrVaultOutsideTheAgentsGrant_Then_UpdateIsNotGranted()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var grantedEntry = await scenario.CreateEntryAsync();
        var otherEntry = await scenario.CreateEntryAsync();
        await scenario.GrantAsync(agent, grantedEntry);
        var otherVault = await Shared.Seeders.VaultSeeder.SeedVaultAsync(
            apiFactory.Services, scenario.OrganizationId, scenario.Owner.User.Id);
        var otherVaultTarget = scenario.NewSubmission(agent,
            target: new AgentSubmissionTargetContract("update", VaultId: otherVault.Id.ToString("D"),
                EntryId: grantedEntry.ToString("D"), ExpectedEntryRevision: "1"),
            submissionKind: 2);

        // When
        var otherEntryResponse = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, otherEntry, 1));
        var otherVaultResponse = await AgentSubmissionScenario.PostSubmissionAsync(agent, otherVaultTarget);

        // Then
        otherEntryResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(otherEntryResponse)).ShouldBe("update-not-granted");
        otherVaultResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(otherVaultResponse)).ShouldBe("update-not-granted");
    }

    [Fact]
    public async Task When_GrantIsRevokedBeforeSubmission_Then_UpdateIsNotGranted()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = await scenario.GrantAsync(agent, entryId);
        (await scenario.Owner.Client.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = scenario.VaultId, GrantId = grantId })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("update-not-granted");
    }

    [Fact]
    public async Task When_GrantIsRevokedAfterSubmission_Then_ContractedUseAndHumanApprovalStayAvailable()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var entryId = await scenario.CreateEntryAsync();
        var grantId = await scenario.GrantAsync(agent, entryId);
        var submissionId = await scenario.SubmitAsync(agent, scenario.UpdateSubmission(agent, entryId, 1));
        (await scenario.Owner.Client.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = scenario.VaultId, GrantId = grantId })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        var use = await agent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var refreshRevoked = await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{submissionId}/approve-update",
            new ApproveSubmissionAsUpdateRequest
            {
                SubmissionId = submissionId,
                Entry = EntryEnvelopeFaker.CreateUpdateRequest(scenario.OrganizationId, scenario.VaultId, entryId, 1) with
                {
                    GrantEnvelopes =
                    [
                        GrantEnvelopeTestData.Contract(scenario.OrganizationId, scenario.VaultId, grantId, entryId,
                            agent.X25519PublicKey, entryRevision: 2, envelopeRevision: 2, grantKeyVersion: 2,
                            agentId: agent.Id, methods: GrantMethods.Inject),
                    ],
                },
            }, TestContext.Current.CancellationToken);
        var approve = await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{submissionId}/approve-update",
            new ApproveSubmissionAsUpdateRequest
            {
                SubmissionId = submissionId,
                Entry = EntryEnvelopeFaker.CreateUpdateRequest(scenario.OrganizationId, scenario.VaultId, entryId, 1),
            }, TestContext.Current.CancellationToken);

        // Then
        use.StatusCode.ShouldBe(HttpStatusCode.OK);
        refreshRevoked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        approve.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.SingleAsync(x => x.Id == grantId, TestContext.Current.CancellationToken)).Status.ShouldBe(GrantStatus.Revoked);
        (await db.GrantEntryEnvelopes.AnyAsync(x => x.GrantId == grantId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentIsDeactivatedAfterSubmission_Then_ItCanNeitherSubmitNorUseButHumansCanStillDecide()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submissionId = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        var next = scenario.NewSubmission(agent);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var agents = scope.ServiceProvider.GetRequiredService<AgentsDbWriteContext>();
            await agents.Agents.Where(x => x.Id == agent.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, AgentStatus.Deactivated),
                    TestContext.Current.CancellationToken);
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await vault.Agents.Where(x => x.Id == agent.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, AgentStatus.Deactivated),
                    TestContext.Current.CancellationToken);
        }

        // When
        var submit = await AgentSubmissionScenario.PostSubmissionAsync(agent, next);
        var use = await agent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken);
        var deny = await scenario.Owner.Client.PostAsync($"api/submissions/{submissionId}/deny", null, TestContext.Current.CancellationToken);

        // Then
        submit.IsSuccessStatusCode.ShouldBeFalse();
        use.IsSuccessStatusCode.ShouldBeFalse();
        deny.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var assertion = apiFactory.Services.CreateAsyncScope();
        var db = assertion.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.AgentEntrySubmissions.AnyAsync(x => x.Id == Guid.Parse(next.SubmissionId), TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_ApproveAndDenyRace_Then_ExactlyOneDecisionCommitsConsistently()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var second = await scenario.AddApproverMemberAsync();
        (await scenario.AddApproversAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var submissionId = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        var entryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);

        // When
        var responses = await Task.WhenAll(
            scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{submissionId}/approve-new",
                new ApproveSubmissionAsNewEntryRequest
                {
                    SubmissionId = submissionId,
                    VaultId = scenario.VaultId,
                    Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, entryId),
                }, TestContext.Current.CancellationToken),
            second.Client.PostAsync($"api/submissions/{submissionId}/deny", null, TestContext.Current.CancellationToken));

        // Then
        responses.Count(x => x.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(x => x.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(loser)).ShouldBe("submission-already-decided");
        var stored = await scenario.LoadSubmissionAsync(submissionId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var entryExists = await db.Entries.AnyAsync(x => x.Id == entryId, TestContext.Current.CancellationToken);
        if (stored.State == AgentSubmissionState.Approved)
        {
            entryExists.ShouldBeTrue();
            stored.Document.ShouldBeNull();
        }
        else
        {
            stored.State.ShouldBe(AgentSubmissionState.Trashed);
            entryExists.ShouldBeFalse();
            stored.Document.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task When_IdenticalApproveNewWithGrantRacesThreeTimes_Then_LosersGetSubmissionAlreadyDecided()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submissionId = await scenario.SubmitAsync(agent, scenario.NewSubmission(agent));
        var entryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);
        var grantId = Guid.NewGuid();
        var request = new ApproveSubmissionAsNewEntryRequest
        {
            SubmissionId = submissionId,
            VaultId = scenario.VaultId,
            Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, entryId),
            Grant = scenario.GrantRequest(agent, entryId, grantId),
        };

        // When
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{submissionId}/approve-new", request,
                TestContext.Current.CancellationToken)));

        // Then
        var codes = await Task.WhenAll(responses
            .Where(x => x.StatusCode != HttpStatusCode.OK)
            .Select(async x => $"{(int)x.StatusCode}:{await AgentSubmissionScenario.ErrorCodeAsync(x)}"));
        responses.Count(x => x.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        codes.ShouldAllBe(code => code == "409:submission-already-decided");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Entries.CountAsync(x => x.Id == entryId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Grants.CountAsync(x => x.Id == grantId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await scenario.LoadSubmissionAsync(submissionId)).DecisionGrantId.ShouldBe(grantId);
    }

    [Fact]
    public async Task When_IdenticalSubmissionIsPostedConcurrently_Then_OneRowExistsAndBothCallersSeeIt()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent);

        // When
        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => AgentSubmissionScenario.PostSubmissionAsync(agent, submission)));

        // Then
        responses.ShouldAllBe(x => x.StatusCode == HttpStatusCode.Created || x.StatusCode == HttpStatusCode.OK);
        responses.Count(x => x.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.AgentEntrySubmissions.CountAsync(x => x.Id == Guid.Parse(submission.SubmissionId),
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Theory]
    [InlineData("submissionKind")]
    [InlineData("targetKindMismatch")]
    [InlineData("uppercaseUuid")]
    [InlineData("leadingZeroRevision")]
    [InlineData("revisionTwoOnCreate")]
    [InlineData("payloadTooShort")]
    [InlineData("payloadTooLong")]
    [InlineData("shortWrappedDek")]
    [InlineData("nonCanonicalBase64")]
    [InlineData("updateWithoutExpectedRevision")]
    public async Task When_SignedSubmissionViolatesClosedShapeOrLimits_Then_ItIsRejectedBeforePersistence(string violation)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var valid = scenario.NewSubmission(agent);
        var invalid = AgentSubmissionScenario.Sign(agent, violation switch
        {
            "submissionKind" => valid with { SubmissionKind = 3 },
            "targetKindMismatch" => valid with { SubmissionKind = 2 },
            "uppercaseUuid" => valid with { SubmissionId = valid.SubmissionId.ToUpperInvariant() },
            "leadingZeroRevision" => valid with { ApproverManifestRevision = "01" },
            "revisionTwoOnCreate" => valid with { SubmissionRevision = "2" },
            "payloadTooShort" => valid with { EncodedPayload = AgentSubmissionScenario.Encode(new byte[39]) },
            "payloadTooLong" => valid with { EncodedPayload = AgentSubmissionScenario.Encode(new byte[24 + 32_768 + 16 + 1]) },
            "shortWrappedDek" => valid with { EncodedAgentWrappedDek = AgentSubmissionScenario.Encode(new byte[119]) },
            "nonCanonicalBase64" => valid with { EncodedApproverWrappedDek = Convert.ToBase64String(Enumerable.Repeat((byte)0xFB, 120).ToArray()) },
            _ => valid with
            {
                SubmissionKind = 2,
                Target = new AgentSubmissionTargetContract("update", VaultId: scenario.VaultId.ToString("D"),
                    EntryId: Guid.NewGuid().ToString("D")),
            },
        });

        // When
        var response = await AgentSubmissionScenario.PostSubmissionAsync(agent, invalid);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("invalid-input");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.AgentEntrySubmissions.AnyAsync(x => x.OrganizationId == scenario.OrganizationId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("zeroAccessEpoch")]
    [InlineData("targetIsNotAnObject")]
    [InlineData("stringKeyVersion")]
    [InlineData("missingField")]
    public async Task When_SubmissionBreaksWireEncodingOverHttp_Then_ItIsRejectedWith400BeforePersistence(string violation)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var valid = scenario.NewSubmission(agent);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(violation == "zeroAccessEpoch"
            ? AgentSubmissionScenario.Sign(agent, valid with { AgentAccessEpoch = "0" })
            : valid, JsonSerializerOptions.Web))!.AsObject();
        switch (violation)
        {
            case "targetIsNotAnObject":
                node["target"] = "new";
                break;
            case "stringKeyVersion":
                node["agentKeyVersion"] = "1";
                break;
            case "missingField":
                node.Remove("approverManifestRevision");
                break;
        }

        // When
        var response = await agent.Client.PostAsJsonAsync("api/agent/submissions", node, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.AgentEntrySubmissions.AnyAsync(x => x.OrganizationId == scenario.OrganizationId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ConsumedChallengeIsReused_Then_TheNextKeySetChangeIsRejected()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var challenge = await scenario.ChallengeAsync(scenario.Owner);
        await scenario.BootstrapAsync();

        // When
        var reused = await scenario.Owner.Client.PostAsJsonAsync(
            "api/approver-keys/rotate",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, scenario.CurrentManifest!, []),
            TestContext.Current.CancellationToken);

        // Then
        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(reused)).ShouldBe("challenge-invalid");
    }

    [Fact]
    public async Task When_TheWholeLifecycleRuns_Then_NoCiphertextWrapOrSignatureReachesMessagesAnalyticsOrLogs()
    {
        // Given
        var harness = apiFactory.Services.GetRequiredService<ITestHarness>();
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var submission = scenario.NewSubmission(agent, payloadSeed: Random.Shared.Next(1, 200));
        var submissionId = await scenario.SubmitAsync(agent, submission);
        (await agent.Client.GetAsync($"api/agent/submissions/{submissionId}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rewrapped = scenario.Rewrap(agent, submission);
        (await AgentSubmissionScenario.PutRewrapAsync(agent, rewrapped)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.Owner.Client.PostAsync($"api/submissions/{submissionId}/deny", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await scenario.Owner.Client.PostAsync($"api/submissions/{submissionId}/restore", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var entryId = await scenario.ReserveEntryIdAsync(scenario.Owner.Client);
        (await scenario.Owner.Client.PostAsJsonAsync($"api/submissions/{submissionId}/approve-new",
            new ApproveSubmissionAsNewEntryRequest
            {
                SubmissionId = submissionId,
                VaultId = scenario.VaultId,
                Entry = EntryEnvelopeFaker.CreateRequest(scenario.OrganizationId, scenario.VaultId, entryId),
            }, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var secrets = new[]
        {
            submission.EncodedPayload, submission.EncodedAgentWrappedDek, submission.EncodedApproverWrappedDek,
            submission.AgentSignature, rewrapped.EncodedAgentWrappedDek, rewrapped.EncodedApproverWrappedDek,
            rewrapped.AgentSignature,
        }.SelectMany(secret => new[]
        {
            secret, Convert.ToBase64String(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(secret)),
        }).ToArray();

        // When
        await harness.Published.Any<Palladin.Module.Vault.Contracts.Events.AgentSubmissionChangedEvent>(
            x => x.Context.Message.SubmissionId == submissionId
                 && x.Context.Message.Action == AgentSubmissionAction.Approved,
            TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var messages = harness.Published.Select(_ => true, TestContext.Current.CancellationToken)
            .Select(x => JsonSerializer.Serialize(x.MessageObject, x.MessageType))
            .ToList();
        var submissionMessages = messages.Where(x => x.Contains(submissionId.ToString())).ToList();
        var analytics = string.Join('\n', apiFactory.AnalyticsService.ReceivedCalls()
            .Select(call => JsonSerializer.Serialize(call.GetArguments())));
        var logs = CapturedLogSink.Snapshot();

        // Then
        submissionMessages.Count.ShouldBeGreaterThanOrEqualTo(7);
        logs.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            messages.ShouldAllBe(message => !message.Contains(secret));
            analytics.ShouldNotContain(secret);
            logs.ShouldAllBe(line => !line.Contains(secret));
        }
    }

    private async Task<string> VaultStateAsync(AgentSubmissionScenario scenario)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var entries = await db.Entries.Where(x => x.OrganizationId == scenario.OrganizationId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, Revision = x.CurrentRevision.Value, x.State, x.UpdatedAt })
            .ToListAsync(TestContext.Current.CancellationToken);
        var versions = await db.EntryVersions.CountAsync(x => x.OrganizationId == scenario.OrganizationId,
            TestContext.Current.CancellationToken);
        var vault = await db.Vaults.Where(x => x.Id == scenario.VaultId)
            .Select(x => new { Sequence = x.MemberSequence.Value, x.UpdatedAt })
            .SingleAsync(TestContext.Current.CancellationToken);
        var grants = await db.Grants.Where(x => x.OrganizationId == scenario.OrganizationId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.QueryCount })
            .ToListAsync(TestContext.Current.CancellationToken);
        var envelopes = await db.GrantEntryEnvelopes
            .Where(x => grants.Select(g => g.Id).Contains(x.GrantId))
            .Select(x => new { x.GrantId, x.EntryRevision })
            .ToListAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Serialize(new { entries, versions, vault, grants, envelopes });
    }
}
