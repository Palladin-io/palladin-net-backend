using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;
using Palladin.Core.Types;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Drives the HTTP API with the protocol's deterministic vectors so backend verification of manifests,
// approver set digest, wraps and Agent submissions is proven against the reference implementation.
[Collection<ApiFactoryCollection>]
public sealed class AgentSubmissionFixtureFlowTests(ApiFactory apiFactory) : TestBase
{
    private static AgentSubmissionFixture Fixture => AgentSubmissionFixture.Current;

    [Fact]
    public async Task When_ProtocolVectorsAreReplayedOverHttp_Then_BootstrapSubmitRotateAndRewrapAreAccepted()
    {
        // Given
        var (member, agent) = await ArrangeAsync();
        var firstManifest = Fixture.Manifest(0);
        var secondManifest = Fixture.Manifest(1);

        // When
        var challenge = await AtAsync(firstManifest.IssuedAt, () => ChallengeAsync(member));
        var bootstrap = await AtAsync(firstManifest.IssuedAt, () => member.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, firstManifest, Fixture.ApproverKeyWraps),
            TestContext.Current.CancellationToken));
        var submit = await agent.PostAsJsonAsync(
            "api/agent/submissions", JsonNode.Parse(Fixture.SubmissionJson(0)), TestContext.Current.CancellationToken);
        var rotationChallenge = await AtAsync(secondManifest.IssuedAt, () => ChallengeAsync(member));
        var rotate = await AtAsync(secondManifest.IssuedAt, () => member.PostAsJsonAsync(
            "api/approver-keys/rotate",
            new ApproverKeySetChangeRequest(rotationChallenge.ChallengeId, secondManifest, RotationWraps()),
            TestContext.Current.CancellationToken));
        var rewrap = await agent.PutAsJsonAsync(
            $"api/agent/submissions/{Fixture.Submission(1).SubmissionId}/wrap",
            JsonNode.Parse(Fixture.SubmissionJson(1)),
            TestContext.Current.CancellationToken);
        var manifests = await agent.GetFromJsonAsync<ListApproverManifestsResponse>(
            "api/agent/approver-manifests", TestContext.Current.CancellationToken);

        // Then
        challenge.ApproverSetDigest.ShouldBe(firstManifest.ApproverSetDigest);
        challenge.ApproverSetRevision.ShouldBe(firstManifest.ApproverSetRevision);
        challenge.IssuedAt.ShouldBe(firstManifest.IssuedAt);
        bootstrap.StatusCode.ShouldBe(HttpStatusCode.OK, await bootstrap.Content.ReadAsStringAsync());
        submit.StatusCode.ShouldBe(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        rotate.StatusCode.ShouldBe(HttpStatusCode.OK, await rotate.Content.ReadAsStringAsync());
        rewrap.StatusCode.ShouldBe(HttpStatusCode.OK, await rewrap.Content.ReadAsStringAsync());
        manifests!.Manifests.ShouldBe([firstManifest, secondManifest]);
        var served = await agent.GetStringAsync(
            $"api/agent/submissions/{Fixture.Submission(1).SubmissionId}", TestContext.Current.CancellationToken);
        JsonNode.DeepEquals(JsonNode.Parse(served), JsonNode.Parse(Fixture.SubmissionJson(1))).ShouldBeTrue();
    }

    [Fact]
    public async Task When_ProtocolWrapsAreServed_Then_TheyRoundTripByteForByteToTheirRecipient()
    {
        // Given
        var (member, _) = await ArrangeAsync();
        var manifest = Fixture.Manifest(0);
        var challenge = await AtAsync(manifest.IssuedAt, () => ChallengeAsync(member));
        (await AtAsync(manifest.IssuedAt, () => member.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest, Fixture.ApproverKeyWraps),
            TestContext.Current.CancellationToken))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var (response, keys) = await member
            .GETAsync<GetApproverKeysEndpoint, EmptyRequest, GetApproverKeysResponse>(new EmptyRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        keys!.Manifest.ShouldBe(manifest);
        keys.MyWraps!.OrderBy(x => x.Descriptor.Purpose).ShouldBe(Fixture.ApproverKeyWraps.OrderBy(x => x.Descriptor.Purpose));
    }

    [Fact]
    public async Task When_ProtocolSubmissionIsReplayedWithForeignOrganization_Then_SignatureOrContextCheckFailsClosed()
    {
        // Given
        var (member, agent) = await ArrangeAsync();
        var manifest = Fixture.Manifest(0);
        var challenge = await AtAsync(manifest.IssuedAt, () => ChallengeAsync(member));
        (await AtAsync(manifest.IssuedAt, () => member.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest, Fixture.ApproverKeyWraps),
            TestContext.Current.CancellationToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var tampered = JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject();
        tampered["submissionId"] = "1d1d1d1d-1d1d-4d1d-8d1d-1d1d1d1d1d1d";

        // When
        var response = await agent.PostAsJsonAsync("api/agent/submissions", tampered, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentSubmissionScenario.ErrorCodeAsync(response)).ShouldBe("invalid-agent-signature");
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

    private async Task<(HttpClient Member, HttpClient Agent)> ArrangeAsync()
    {
        var organizationId = Fixture.OrganizationId;
        await DeleteFixtureStateAsync();

        var user = await FixtureMemberAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, Fixture.MemberX25519PublicKey, Fixture.MemberKeyVersion);
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
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organizationId, id: Fixture.AgentId, publicKey: x25519,
            signingPublicKey: ed25519, recipientKeyVersion: Fixture.AgentKeyVersion, accessEpoch: Fixture.AgentAccessEpoch);
        var grant = GrantFaker.CreateGranular(vaultId: Fixture.VaultId, organizationId: organizationId, agentId: agent.Id,
            entryId: Fixture.EntryId, agentAccessEpoch: Fixture.AgentAccessEpoch, createdBy: user.Id).Generate();
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(organizationId, Fixture.VaultId, grant.Id, Fixture.EntryId,
            grant.Methods, grant.ExpiresAt, grant.QueryLimit, entryRevision: 7, agentId: agent.Id, agentPublicKey: x25519));
        await apiFactory.Services.SeedGranularGrantAsync(grant);

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

    private static List<ApproverKeyWrapContract> RotationWraps()
    {
        var fingerprint = AgentSubmissionScenario.Encode(MemberKeyFingerprint.Compute(Fixture.MemberX25519PublicKey));
        return new ushort[] { 8, 9 }.Select(purpose => new ApproverKeyWrapContract(
            new ApproverKeyWrapDescriptorContract(purpose, 33, Fixture.OrganizationId.ToString("D"),
                Fixture.MemberId.ToString("D"), "2", 2, 5, Fixture.MemberKeyVersion, fingerprint),
            AgentSubmissionScenario.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(120))))
            .ToList();
    }
}
