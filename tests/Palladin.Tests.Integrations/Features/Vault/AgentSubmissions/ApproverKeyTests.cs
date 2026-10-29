using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

[Collection<ApiFactoryCollection>]
public sealed class ApproverKeyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ApproverBootstraps_Then_KeySetManifestAndOwnWrapsAreServed()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);

        // When
        await scenario.BootstrapAsync();

        // Then
        var (response, keys) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        keys!.Status.ShouldBe("ready");
        keys.KeyVersion.ShouldBe(1u);
        keys.ApproverSetRevision.ShouldBe("1");
        keys.Manifest.ShouldBe(scenario.CurrentManifest);
        keys.MyWraps!.Select(x => x.Descriptor.Purpose).Order().ShouldBe([(ushort)8, (ushort)9]);
        keys.MyWraps.ShouldAllBe(x => x.Descriptor.MemberId == scenario.Owner.User.Id.ToString("D"));
        keys.Approvers.Single().HasWrap.ShouldBeTrue();
        keys.RotationRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task When_KeySetIsAbsent_Then_StatusIsAbsentAndAgentsGetNoApprovers()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();

        // When
        var (response, keys) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        var manifests = await agent.Client.GetAsync("api/agent/approver-manifests", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        keys!.Status.ShouldBe("absent");
        keys.MyWraps.ShouldBeNull();
        manifests.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AgentSubmissionScenario.ErrorCodeAsync(manifests)).ShouldBe("no-approvers");
    }

    [Fact]
    public async Task When_TwoApproversBootstrapConcurrently_Then_ExactlyOneKeySetWins()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var second = await scenario.AddApproverMemberAsync();

        // When
        var results = await Task.WhenAll(
            scenario.BootstrapRequestAsync(scenario.Owner),
            scenario.BootstrapRequestAsync(second));

        // Then
        var statuses = results.Select(x => x.StatusCode).ToArray();
        statuses.Count(x => x == HttpStatusCode.OK).ShouldBe(1);
        var loser = results.Single(x => x.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(loser)).ShouldBeOneOf("approver-keys-exist", "approver-set-changed");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.OrganizationApproverManifests.CountAsync(
            x => x.OrganizationId == scenario.OrganizationId, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_BootstrapIsRepeated_Then_ExistingKeySetIsNeverReplaced()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();
        var original = scenario.CurrentManifest;

        // When
        var response = await scenario.BootstrapRequestAsync(scenario.Owner);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-keys-exist");
        scenario.CurrentManifest.ShouldBe(original);
    }

    [Fact]
    public async Task When_ChallengeIsReusedOrExpired_Then_BootstrapIsRejected()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var challenge = await scenario.ChallengeAsync(scenario.Owner);
        var start = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Reset(start + Duration.FromMinutes(6));
        try
        {
            // When
            var response = await scenario.Owner.Client.PostAsJsonAsync(
                "api/approver-keys/bootstrap",
                new ApproverKeySetChangeRequest(challenge.ChallengeId, null!, []),
                TestContext.Current.CancellationToken);

            // Then
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("challenge-invalid");
        }
        finally
        {
            apiFactory.FakeClock.Reset(start);
        }
    }

    [Fact]
    public async Task When_ApproverSetChangesAfterChallenge_Then_ManifestIsRejected()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var challenge = await scenario.ChallengeAsync(scenario.Owner);
        await scenario.AddApproverMemberAsync();

        // When
        var response = await scenario.Owner.Client.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, null!, []),
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-set-changed");
    }

    [Fact]
    public async Task When_NewApproverIsAdded_Then_ExistingKeyVersionIsWrappedForThemWithoutRotation()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();
        var newcomer = await scenario.AddApproverMemberAsync();
        var (_, beforeAdd) = await newcomer.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());

        // When
        var response = await scenario.AddApproversAsync(scenario.Owner);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        beforeAdd!.MyWraps.ShouldBeEmpty();
        var (_, afterAdd) = await newcomer.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        afterAdd!.KeyVersion.ShouldBe(1u);
        afterAdd.ApproverSetRevision.ShouldBe("2");
        afterAdd.Manifest!.ManifestRevision.ShouldBe("2");
        afterAdd.MyWraps!.Count.ShouldBe(2);
        afterAdd.Approvers.ShouldAllBe(x => x.HasWrap);
    }

    [Fact]
    public async Task When_PendingApproverWithoutWrapTriesToAddOthers_Then_TheyCannotSignTheManifest()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();
        var newcomer = await scenario.AddApproverMemberAsync();

        // When
        var response = await scenario.AddApproversAsync(newcomer);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-not-current");
    }

    [Fact]
    public async Task When_ApproverIsRemoved_Then_WrapsStopBeingServedAndRotationReplacesEveryWrapAtomically()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var removed = await scenario.AddApproverMemberAsync();
        await scenario.BootstrapAsync();
        await scenario.SetApproverAsync(removed.User.Id, false);

        // When
        var (_, removedView) = await removed.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        var (_, ownerBefore) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        var addResponse = await scenario.AddApproversAsync(scenario.Owner);
        var rotateResponse = await scenario.RotateAsync(scenario.Owner);

        // Then
        removedView!.MyWraps.ShouldBeEmpty();
        ownerBefore!.RotationRequired.ShouldBeTrue();
        addResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        rotateResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await rotateResponse.Content.ReadAsStringAsync());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var wraps = await db.ApproverKeyWraps.Where(x => x.OrganizationId == scenario.OrganizationId)
            .ToListAsync(TestContext.Current.CancellationToken);
        wraps.ShouldAllBe(x => x.KeyVersion == 2 && x.MemberId == scenario.Owner.User.Id);
        wraps.Count.ShouldBe(2);
        var (_, ownerAfter) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        ownerAfter!.KeyVersion.ShouldBe(2u);
        ownerAfter.RotationRequired.ShouldBeFalse();
        ownerAfter.Manifest!.PreviousSigningKeyFingerprint.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_ApproverMemberKeyChanges_Then_TheirWrapIsWithheldUntilRotation()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var rekeyed = await scenario.AddApproverMemberAsync();
        await scenario.BootstrapAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(rekeyed.User.Id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 2);

        // When
        var (_, view) = await rekeyed.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        var rotateByRekeyed = await scenario.RotateAsync(rekeyed);
        var rotateByOwner = await scenario.RotateAsync(scenario.Owner);

        // Then
        view!.MyWraps.ShouldBeEmpty();
        view.RotationRequired.ShouldBeTrue();
        rotateByRekeyed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        rotateByOwner.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, afterRotation) = await rekeyed.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        afterRotation!.MyWraps!.Count.ShouldBe(2);
        afterRotation.MyWraps.ShouldAllBe(x => x.Descriptor.RecipientKeyVersion == 2 && x.Descriptor.WrappedKeyVersion == 2);
    }

    [Fact]
    public async Task When_RotationContinuityIsForged_Then_RotationIsRejectedAndNothingChanges()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();

        // When
        var response = await scenario.RotateAsync(scenario.Owner, forgeContinuity: true);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("manifest-continuity-invalid");
        var (_, keys) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        keys!.KeyVersion.ShouldBe(1u);
        keys.MyWraps!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task When_RotationOmitsAnApproverWrap_Then_NoPartialRotationCommits()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.AddApproverMemberAsync();
        await scenario.BootstrapAsync();
        var challenge = await scenario.ChallengeAsync(scenario.Owner);

        // When
        var response = await scenario.Owner.Client.PostAsJsonAsync(
            "api/approver-keys/rotate",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, scenario.CurrentManifest!, []),
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.ApproverKeyWraps.CountAsync(x => x.OrganizationId == scenario.OrganizationId && x.KeyVersion == 1,
            TestContext.Current.CancellationToken)).ShouldBe(4);
    }

    [Fact]
    public async Task When_ApproverIsAddedAfterRotation_Then_SameVersionManifestCarriesNoContinuity()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await scenario.AddApproverMemberAsync();

        // When
        var withContinuity = await scenario.AddApproversAsync(scenario.Owner, includeContinuity: true);
        var withoutContinuity = await scenario.AddApproversAsync(scenario.Owner);

        // Then
        withContinuity.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(withContinuity)).ShouldBe("continuity-not-allowed");
        withoutContinuity.StatusCode.ShouldBe(HttpStatusCode.OK, await withoutContinuity.Content.ReadAsStringAsync());
        scenario.CurrentManifest!.KeyVersion.ShouldBe(2u);
        scenario.CurrentManifest.ContinuitySignature.ShouldBeNull();
        scenario.CurrentManifest.PreviousSigningKeyFingerprint.ShouldBeNull();
    }

    [Theory]
    [InlineData("messageKey")]
    [InlineData("signingKey")]
    public async Task When_SameVersionManifestChangesAGroupKey_Then_AddApproversIsRejectedAndNothingChanges(string changedKey)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();
        await scenario.AddApproverMemberAsync();
        var current = scenario.CurrentKey!;
        var substituted = changedKey == "messageKey" ? current.WithNewMessageKey() : new ApproverGroupKey(current.KeyVersion);

        // When
        var response = await scenario.AddApproversAsync(scenario.Owner, manifestKey: substituted);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-manifest-stale");
        var (_, keys) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        keys!.Manifest!.ManifestRevision.ShouldBe("1");
        keys.Manifest.MessageKeyFingerprint.ShouldBe(AgentSubmissionScenario.Encode(current.MessageKeyFingerprint));
        keys.Approvers.Count(x => x.HasWrap).ShouldBe(1);
    }

    [Fact]
    public async Task When_RotationSkipsAKeyVersion_Then_TheManifestChainIsNotExtended()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();

        // When
        var response = await scenario.RotateAsync(scenario.Owner, keyVersionStep: 2);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("approver-manifest-stale");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.OrganizationApproverManifests.CountAsync(x => x.OrganizationId == scenario.OrganizationId,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_RotationOmitsContinuity_Then_ItIsRejected()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.BootstrapAsync();

        // When
        var response = await scenario.RotateAsync(scenario.Owner, omitContinuity: true);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("manifest-continuity-invalid");
    }

    [Fact]
    public async Task When_NoCurrentApproverCanOpenTheKeySet_Then_ReBootstrapIntroducesNextVersionWithoutContinuity()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        var pending = scenario.NewSubmission(agent);
        var submissionId = await scenario.SubmitAsync(agent, pending);
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(
            scenario.Owner.User.Id, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 2);
        var (_, unavailable) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());

        // When
        var rotate = await scenario.RotateAsync(scenario.Owner);
        var withContinuity = await scenario.RebootstrapAsync(scenario.Owner, includeContinuity: true);
        var rebootstrap = await scenario.RebootstrapAsync(scenario.Owner);

        // Then
        unavailable!.Status.ShouldBe("unavailable");
        unavailable.RotationRequired.ShouldBeTrue();
        unavailable.MyWraps.ShouldBeEmpty();
        rotate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        withContinuity.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        rebootstrap.StatusCode.ShouldBe(HttpStatusCode.OK, await rebootstrap.Content.ReadAsStringAsync());
        var (_, ready) = await scenario.Owner.Client
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        ready!.Status.ShouldBe("ready");
        ready.KeyVersion.ShouldBe(2u);
        ready.Manifest!.ContinuitySignature.ShouldBeNull();
        ready.MyWraps!.Count.ShouldBe(2);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            (await db.ApproverKeyWraps.Where(x => x.OrganizationId == scenario.OrganizationId)
                .AllAsync(x => x.KeyVersion == 2, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        var manifests = await agent.Client.GetFromJsonAsync<ListApproverManifestsResponse>(
            "api/agent/approver-manifests?afterRevision=1", TestContext.Current.CancellationToken);
        manifests!.Manifests.Single().KeyVersion.ShouldBe(2u);
        (await scenario.LoadSubmissionAsync(submissionId)).State.ShouldBe(Palladin.Core.Types.AgentSubmissionState.Pending);
        (await AgentSubmissionScenario.PutRewrapAsync(agent, scenario.Rewrap(agent, pending)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_KeySetIsAvailableOrAbsent_Then_ReBootstrapIsRejected()
    {
        // Given
        var absent = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var ready = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await ready.BootstrapAsync();

        // When
        var whileReady = await ready.RebootstrapAsync(ready.Owner);
        var whileAbsent = await absent.RebootstrapAsync(absent.Owner);

        // Then
        whileReady.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(whileReady)).ShouldBe("approver-keys-available");
        whileAbsent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AgentSubmissionScenario.ErrorCodeAsync(whileAbsent)).ShouldBe("no-approvers");
        ready.CurrentKey!.KeyVersion.ShouldBe(1u);
    }

    [Fact]
    public async Task When_AgentFetchesManifests_Then_ChainIsReturnedAfterRevisionInOrder()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var agent = await scenario.CreateAgentAsync();
        await scenario.BootstrapAsync();
        (await scenario.RotateAsync(scenario.Owner)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var all = await agent.Client.GetFromJsonAsync<ListApproverManifestsResponse>(
            "api/agent/approver-manifests", TestContext.Current.CancellationToken);
        var afterFirst = await agent.Client.GetFromJsonAsync<ListApproverManifestsResponse>(
            "api/agent/approver-manifests?afterRevision=1", TestContext.Current.CancellationToken);

        // Then
        all!.OrganizationId.ShouldBe(scenario.OrganizationId);
        all.AgentKeyVersion.ShouldBe(agent.KeyVersion);
        all.AgentAccessEpoch.ShouldBe(agent.AccessEpoch.ToString());
        all.Manifests.Select(x => x.ManifestRevision).ShouldBe(["1", "2"]);
        afterFirst!.Manifests.Single().ShouldBe(scenario.CurrentManifest);
        afterFirst.Manifests.Single().ContinuitySignature.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_MemberWithoutApproverEligibilityRequestsChallenge_Then_Forbidden()
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        await scenario.SetApproverAsync(scenario.Owner.User.Id, false);

        // When
        var (response, _) = await scenario.Owner.Client
            .POSTAsync<IssueApproverKeyChallengeEndpoint, EmptyRequest, IssueApproverKeyChallengeResponse>(new EmptyRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("GET", "api/approver-keys")]
    [InlineData("POST", "api/approver-keys/challenge")]
    [InlineData("POST", "api/approver-keys/bootstrap")]
    [InlineData("POST", "api/approver-keys/add-approvers")]
    [InlineData("POST", "api/approver-keys/rotate")]
    [InlineData("POST", "api/approver-keys/rebootstrap")]
    public async Task When_CallerLacksGrantManageOrAuthentication_Then_ApproverKeyEndpointsAreClosed(string method, string path)
    {
        // Given
        var scenario = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var withoutGrantManage = apiFactory.CreateAuthenticatedClient(
            scenario.Owner.User, (Permission)int.MaxValue & ~Permission.GrantManage);
        var agent = await scenario.CreateAgentAsync();

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
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        asAgent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
