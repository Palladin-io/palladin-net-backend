using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;
using Palladin.Core.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// The protocol's deterministic v1 vectors are the conformance test for EXISTING legacy objects: they are seeded as
// persisted state and must stay verifiable, servable, decidable and re-wrappable to a migrated key, while sealing
// any new object on the read-only palladin-x25519-sealed-box-v1 suite fails with key-migration-pending.
[Collection<ApiFactoryCollection>]
public sealed class AgentSubmissionFixtureFlowTests(ApiFactory apiFactory) : TestBase
{
    private static AgentSubmissionFixture Fixture => AgentSubmissionFixture.Current;

    [Fact]
    public async Task When_LegacyKeySetIsSeeded_Then_ManifestAndWrapsAreServedByteForByte()
    {
        // Given
        var (member, agent) = await ArrangeAsync(LegacyState.KeySet);

        // When
        var (response, keys) = await member
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());
        var manifests = await agent.GetFromJsonAsync<ListApproverManifestsResponse>(
            "api/agent/approver-manifests", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        keys!.Status.ShouldBe("ready");
        keys.Manifest.ShouldBe(Fixture.Manifest(0));
        keys.MyWraps!.OrderBy(x => x.Descriptor.Purpose).ShouldBe(Fixture.ApproverKeyWraps.OrderBy(x => x.Descriptor.Purpose));
        manifests!.Manifests.ShouldBe([Fixture.Manifest(0)]);
        manifests.OrganizationId.ShouldBe(Fixture.OrganizationId);
        manifests.AgentKeyVersion.ShouldBe(Fixture.AgentKeyVersion);
        manifests.AgentAccessEpoch.ShouldBe(Fixture.Context["agentAccessEpoch"]!.GetValue<string>());
    }

    [Fact]
    public async Task When_LegacySubmissionIsSeeded_Then_AgentReadsItByteForByteAndApproverDecidesIt()
    {
        // Given
        var (member, agent) = await ArrangeAsync(LegacyState.KeySetAndSubmission);

        // When
        var served = await agent.GetStringAsync(
            $"api/agent/submissions/{Fixture.Submission(0).SubmissionId}", TestContext.Current.CancellationToken);
        var denied = await member.PostAsync(
            $"api/submissions/{Fixture.Submission(0).SubmissionId}/deny", null, TestContext.Current.CancellationToken);

        // Then
        JsonNode.DeepEquals(JsonNode.Parse(served), JsonNode.Parse(Fixture.SubmissionJson(0))).ShouldBeTrue();
        denied.StatusCode.ShouldBe(HttpStatusCode.OK, await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await denied.Content.ReadFromJsonAsync<AgentSubmissionStateResponse>(TestContext.Current.CancellationToken))!
            .State.ShouldBe("trashed");
    }

    [Fact]
    public async Task When_ApproverAndAgentMigrateKeys_Then_AgentRewrapsLegacySubmissionToTheNewKeyVersion()
    {
        // Given
        var (member, agent) = await ArrangeAsync(LegacyState.KeySetAndSubmission);
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(
            Fixture.MemberId, RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PublicKeyBytes), Fixture.MemberKeyVersion + 1);
        var approverKey = new ApproverGroupKey(2);
        var challenge = await ChallengeAsync(member);
        var rebootstrap = await member.PostAsJsonAsync("api/approver-keys/rebootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId,
                AgentSubmissionScenario.SignManifest(Fixture.OrganizationId, approverKey, 2, challenge, previous: null),
                AgentSubmissionScenario.Wraps(Fixture.OrganizationId, challenge.Approvers, 2, challenge.ApproverSetRevision)),
            TestContext.Current.CancellationToken);
        var agentEncryptionKey = VaultAgentFaker.GenerateEncryptionPublicKey();
        await PublishAgentEncryptionKeyAsync(agentEncryptionKey, Fixture.AgentKeyVersion + 1);
        var stored = Fixture.Submission(0);
        var rewrap = AgentSubmissionScenario.Sign(FixtureAgent(agent, Fixture.AgentKeyVersion + 1), stored with
        {
            Schema = AgentSubmissionScenario.SubmissionSchema,
            SubmissionRevision = "2",
            AgentKeyVersion = Fixture.AgentKeyVersion + 1,
            AgentKeyFingerprint = AgentSubmissionScenario.Encode(
                AgentRecipientKey.Fingerprint(KeyWrapperSuites.Current.Id, agentEncryptionKey)),
            ApproverKeyVersion = approverKey.KeyVersion,
            ApproverMessageKeyFingerprint = AgentSubmissionScenario.Encode(approverKey.MessageKeyFingerprint),
            ApproverManifestRevision = "2",
            EncodedAgentWrappedDek = AgentSubmissionScenario.Encode(RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PackageBytes)),
            EncodedApproverWrappedDek = AgentSubmissionScenario.Encode(RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PackageBytes)),
            AgentWrappedDekSuiteId = KeyWrapperSuites.Current.Id,
            ApproverWrappedDekSuiteId = KeyWrapperSuites.Current.Id,
        });

        // When
        var response = await agent.PutAsJsonAsync(
            $"api/agent/submissions/{stored.SubmissionId}/wrap", rewrap, TestContext.Current.CancellationToken);

        // Then
        rebootstrap.StatusCode.ShouldBe(HttpStatusCode.OK, await rebootstrap.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var served = await agent.GetFromJsonAsync<AgentEntrySubmissionContract>(
            $"api/agent/submissions/{stored.SubmissionId}", TestContext.Current.CancellationToken);
        served!.SubmissionRevision.ShouldBe("2");
        served.ApproverKeyVersion.ShouldBe(2u);
        served.ApproverWrappedDekSuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        served.EncodedPayload.ShouldBe(stored.EncodedPayload);
    }

    [Fact]
    public async Task When_LegacySubmissionIsReplayedByForeignOrganizationAgent_Then_ContextCheckFailsClosed()
    {
        // Given
        await ArrangeAsync(LegacyState.KeySetAndSubmission);
        var foreign = await AgentSubmissionScenario.CreateAsync(apiFactory);
        var foreignAgent = await foreign.CreateAgentAsync();

        // When
        var response = await foreignAgent.Client.PostAsJsonAsync(
            "api/agent/submissions", JsonNode.Parse(Fixture.SubmissionJson(0)), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("context-mismatch");
        (await foreign.LoadSubmissionCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task When_ApproverBootstrapsLegacyKeySet_Then_KeyMigrationIsPending()
    {
        // Given
        var (member, _) = await ArrangeAsync(LegacyState.None);
        var manifest = Fixture.Manifest(0);
        var challenge = await AtAsync(manifest.IssuedAt, () => ChallengeAsync(member));

        // When
        var response = await AtAsync(manifest.IssuedAt, () => member.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest, Fixture.ApproverKeyWraps),
            TestContext.Current.CancellationToken));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("key-migration-pending");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().OrganizationApproverKeySets
            .AnyAsync(x => x.OrganizationId == Fixture.OrganizationId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentSubmitsNewLegacySubmission_Then_KeyMigrationIsPending()
    {
        // Given
        var (_, agent) = await ArrangeAsync(LegacyState.KeySet);

        // When
        var response = await agent.PostAsJsonAsync(
            "api/agent/submissions", JsonNode.Parse(Fixture.SubmissionJson(0)), TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("key-migration-pending");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().AgentEntrySubmissions
            .AnyAsync(x => x.OrganizationId == Fixture.OrganizationId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    // The fixture Entry is a structural stub at revision 7; remove it so global lifecycle jobs in other tests
    // never process synthetic history.
    protected override async ValueTask TearDownAsync() => await DeleteFixtureStateAsync();

    private async Task DeleteFixtureStateAsync()
    {
        var organizationId = Fixture.OrganizationId;
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await vault.AgentEntrySubmissions.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
        await vault.ApproverKeyChallenges.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
        await vault.OrganizationApproverKeySets.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
        await vault.OrganizationApprovers.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
        await vault.Grants.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
        await vault.Vaults.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync();
    }

    private enum LegacyState
    {
        None = 0,
        KeySet = 1,
        KeySetAndSubmission = 2,
    }

    // Seeds the v1 vectors as pre-migration state: no new legacy object may be sealed over HTTP, so the
    // approver key set and the pending submission are created through the domain exactly as they were persisted.
    private async Task<(HttpClient Member, HttpClient Agent)> ArrangeAsync(LegacyState legacyState)
    {
        var organizationId = Fixture.OrganizationId;
        await DeleteFixtureStateAsync();

        var user = await FixtureMemberAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(
            user.Id, Fixture.MemberX25519PublicKey, Fixture.MemberKeyVersion, LegacyWrapperSuite.Id);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            vault.OrganizationApprovers.Add(OrganizationApprover.Create(
                organizationId, user.Id, true, apiFactory.FakeClock.GetCurrentInstant()));
            await vault.SaveChangesAsync();
        }

        await apiFactory.Services.SeedVaultAsync(organizationId, user.Id, VaultFaker.Create(Fixture.VaultId, organizationId, user.Id));
        await apiFactory.Services.SeedEntryAsync(Fixture.VaultId, user.Id,
            EntryFaker.Create(Fixture.EntryId, organizationId, Fixture.VaultId, user.Id)
                .RuleFor(x => x.CurrentRevision, new EntryRevision(7)));

        var x25519 = Convert.ToBase64String(Fixture.AgentX25519PublicKey);
        var ed25519 = Convert.ToBase64String(Fixture.AgentEd25519PublicKey);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        await apiFactory.Services.SeedAgentAsync(organizationId,
            AgentFaker.Create(Fixture.AgentId, organizationId, x25519, ed25519)
                .RuleFor(x => x.RecipientKeyVersion, Fixture.AgentKeyVersion)
                .RuleFor(x => x.AccessEpoch, Fixture.AgentAccessEpoch));
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organizationId, id: Fixture.AgentId, publicKey: x25519, publicKeySuiteId: "palladin-x25519-sealed-box-v1",
            signingPublicKey: ed25519, recipientKeyVersion: Fixture.AgentKeyVersion, accessEpoch: Fixture.AgentAccessEpoch);
        var grant = GrantFaker.CreateGranular(vaultId: Fixture.VaultId, organizationId: organizationId, agentId: agent.Id,
            entryId: Fixture.EntryId, agentAccessEpoch: Fixture.AgentAccessEpoch, createdBy: user.Id).Generate();
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.LegacyScope(organizationId, Fixture.VaultId, grant.Id, Fixture.EntryId,
            grant.Methods, grant.ExpiresAt, grant.QueryLimit, entryRevision: 7, agentPublicKey: x25519));
        await apiFactory.Services.SeedGranularGrantAsync(grant);
        if (legacyState != LegacyState.None)
        {
            await SeedLegacyKeySetAsync(legacyState == LegacyState.KeySetAndSubmission);
        }

        return (
            apiFactory.CreateAuthenticatedClient(user),
            apiFactory.CreateSignedAgentClient(Fixture.AgentId, apiKey, x25519, AgentRequestSigning.FromSeed(Fixture.AgentEd25519Seed)));
    }

    private async Task<Palladin.Module.Identity.Domain.User> FixtureMemberAsync()
    {
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
            var existing = await identity.Users.SingleOrDefaultAsync(
                x => x.Id == Fixture.MemberId, TestContext.Current.CancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var (user, _, _) = await apiFactory.Services.SeedUserAsync(
            UserFaker.Create(Fixture.MemberId).RuleFor(x => x.EmailVerified, true),
            OrganizationFaker.Create(Fixture.OrganizationId));
        return user;
    }

    private static async Task<IssueApproverKeyChallengeResponse> ChallengeAsync(HttpClient member)
    {
        var (response, challenge) = await member
            .POSTAsync<IssueApproverKeyChallengeEndpoint, EmptyRequest, IssueApproverKeyChallengeResponse>(new EmptyRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return challenge!;
    }

    private async Task<T> AtAsync<T>(string instant, Func<Task<T>> action)
    {
        var original = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Reset(InstantPattern.General.Parse(instant).Value);
        try
        {
            return await action();
        }
        finally
        {
            apiFactory.FakeClock.Reset(original);
        }
    }

    private async Task SeedLegacyKeySetAsync(bool withSubmission)
    {
        var manifest = Fixture.Manifest(0);
        var issuedAt = InstantPattern.General.Parse(manifest.IssuedAt).Value;
        var wraps = Fixture.ApproverKeyWraps.Select(wrap => ApproverKeyWrap.Create(
            Fixture.OrganizationId,
            Guid.Parse(wrap.Descriptor.MemberId),
            wrap.Descriptor.Purpose,
            wrap.Descriptor.WrappedKeyVersion,
            ulong.Parse(wrap.Descriptor.ResourceRevision),
            new MemberRecipientKeyVersion(wrap.Descriptor.RecipientKeyVersion),
            WebEncoders.Base64UrlDecode(wrap.Descriptor.RecipientFingerprint),
            LegacyWrapperSuite.Id,
            WebEncoders.Base64UrlDecode(wrap.EncodedSealedKeyPackage))).ToList();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        vault.OrganizationApproverKeySets.Add(
            OrganizationApproverKeySet.Bootstrap(AgentSubmissionCrypto.ParseManifest(manifest), wraps, issuedAt));
        if (withSubmission)
        {
            var submission = AgentSubmissionCrypto.ParseSubmission(Fixture.Submission(0));
            vault.AgentEntrySubmissions.Add(AgentEntrySubmission.Submit(
                submission.OrganizationId,
                submission.SubmissionId,
                submission.AgentId,
                submission.Kind,
                submission.EntryType,
                submission.Target,
                Fixture.AgentAccessEpoch,
                submission.ApproverKeyVersion,
                submission.Document,
                issuedAt));
        }

        await vault.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // The Agents module re-publishes the Agent with its new X-Wing encryption key; Vault applies it to the replica.
    private async Task PublishAgentEncryptionKeyAsync(string encryptionPublicKey, uint keyVersion)
    {
        var now = SystemClock.Instance.GetCurrentInstant() + Duration.FromSeconds(1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        await new OnAgentUpserted(scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>())
            .Consume(apiFactory.MockConsumeContext(new AgentUpsertedEvent(
                Fixture.AgentId,
                Fixture.OrganizationId,
                AgentStatus.Active,
                Convert.ToBase64String(Fixture.AgentX25519PublicKey),
                keyVersion,
                encryptionPublicKey,
                KeyWrapperSuites.Current.Id,
                Convert.ToBase64String(Fixture.AgentEd25519PublicKey),
                "fixture-agent",
                "runtime",
                null,
                null,
                Fixture.AgentAccessEpoch,
                now,
                now)));
    }

    private static ScenarioAgent FixtureAgent(HttpClient client, uint keyVersion) => new(
        Fixture.AgentId,
        client,
        AgentRequestSigning.FromSeed(Fixture.AgentEd25519Seed),
        Convert.ToBase64String(Fixture.AgentX25519PublicKey),
        keyVersion,
        Fixture.AgentAccessEpoch);
}
