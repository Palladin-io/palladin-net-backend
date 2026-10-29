using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSec.Cryptography;
using Palladin.Core.Types;
using User = Palladin.Module.Identity.Domain.User;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Shared;

// One approver group key version held by the test client: a real Ed25519 signing key (signatures are
// verified by the backend) and an X25519 message public key (the backend only checks its fingerprint).
internal sealed class ApproverGroupKey
{
    private readonly Key _signingKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
    {
        ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
    });

    internal ApproverGroupKey(uint keyVersion) => KeyVersion = keyVersion;

    internal uint KeyVersion { get; }
    internal byte[] MessagePublicKey { get; } = RandomNumberGenerator.GetBytes(32);
    internal byte[] SigningPublicKey => _signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    internal byte[] MessageKeyFingerprint =>
        VaultKeyFingerprint.Compute(MessagePublicKey, VaultKeyKind.OrganizationApproverX25519);
    internal byte[] SigningKeyFingerprint =>
        VaultKeyFingerprint.Compute(SigningPublicKey, VaultKeyKind.OrganizationApproverEd25519);

    internal byte[] Sign(byte[] input) => SignatureAlgorithm.Ed25519.Sign(_signingKey, input);
}

internal sealed record ScenarioMember(User User, HttpClient Client, byte[] PublicKey);

internal sealed record ScenarioAgent(
    Guid Id,
    HttpClient Client,
    AgentRequestSigning Signing,
    string X25519PublicKey,
    uint KeyVersion,
    uint AccessEpoch);

internal sealed class AgentSubmissionScenario
{
    internal const string ManifestSchema = "palladin.organization-approver-manifest.v1";
    internal const string SubmissionSchema = "palladin.agent-entry-submission.v1";

    private readonly ApiFactory _apiFactory;
    private readonly List<OrganizationApproverManifestContract> _manifests = [];

    private AgentSubmissionScenario(ApiFactory apiFactory, Guid organizationId, ScenarioMember owner, Guid vaultId)
    {
        _apiFactory = apiFactory;
        OrganizationId = organizationId;
        Owner = owner;
        VaultId = vaultId;
    }

    internal Guid OrganizationId { get; }
    internal ScenarioMember Owner { get; }
    internal Guid VaultId { get; }
    internal ApproverGroupKey? CurrentKey { get; private set; }
    internal OrganizationApproverManifestContract? CurrentManifest => _manifests.LastOrDefault();

    internal static async Task<AgentSubmissionScenario> CreateAsync(ApiFactory apiFactory)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var owner = await RegisterApproverAsync(apiFactory, user, organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        return new AgentSubmissionScenario(apiFactory, organization.Id, owner, vault.Id);
    }

    internal async Task<ScenarioMember> AddApproverMemberAsync()
    {
        var user = await _apiFactory.Services.SeedAdditionalOrganizationMemberAsync(OrganizationId);
        return await RegisterApproverAsync(_apiFactory, user, OrganizationId);
    }

    internal async Task<ScenarioAgent> CreateAgentAsync(AgentStatus status = AgentStatus.Active)
    {
        var (_, apiKey) = await _apiFactory.Services.SeedApiKeyAsync(OrganizationId);
        var signing = AgentRequestSigning.Generate();
        var x25519 = AgentFaker.GeneratePublicKey();
        var agent = await _apiFactory.Services.SeedAgentAsync(
            OrganizationId,
            AgentFaker.Create(organizationId: OrganizationId, publicKey: x25519, signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, status));
        await _apiFactory.Services.SeedVaultAgentAsync(
            OrganizationId, status, id: agent.Id, publicKey: x25519, signingPublicKey: signing.PublicKeyBase64);
        return new ScenarioAgent(
            agent.Id,
            _apiFactory.CreateSignedAgentClient(agent.Id, apiKey, x25519, signing),
            signing,
            x25519,
            1,
            1);
    }

    internal async Task<IssueApproverKeyChallengeResponse> ChallengeAsync(ScenarioMember member)
    {
        var (response, challenge) = await member.Client
            .POSTAsync<IssueApproverKeyChallengeEndpoint, EmptyRequest, IssueApproverKeyChallengeResponse>(new EmptyRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return challenge!;
    }

    internal async Task BootstrapAsync(ScenarioMember? member = null)
    {
        var response = await BootstrapRequestAsync(member ?? Owner);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    internal async Task<HttpResponseMessage> BootstrapRequestAsync(ScenarioMember member)
    {
        var key = new ApproverGroupKey(1);
        var challenge = await ChallengeAsync(member);
        var manifest = SignManifest(key, 1, challenge, previous: null);
        var response = await member.Client.PostAsJsonAsync(
            "api/approver-keys/bootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest, Wraps(challenge.Approvers, 1, challenge.ApproverSetRevision)),
            TestContext.Current.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            CurrentKey = key;
            _manifests.Add(manifest);
        }

        return response;
    }

    internal async Task<HttpResponseMessage> AddApproversAsync(ScenarioMember signer, bool includeContinuity = false)
    {
        var challenge = await ChallengeAsync(signer);
        var manifest = SignManifest(CurrentKey!, ulong.Parse(CurrentManifest!.ManifestRevision) + 1, challenge,
            previous: includeContinuity ? CurrentKey : null);
        var newApprovers = challenge.Approvers.Where(x => !x.HasWrap).ToList();
        var response = await signer.Client.PostAsJsonAsync(
            "api/approver-keys/add-approvers",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest,
                Wraps(newApprovers, CurrentKey!.KeyVersion, challenge.ApproverSetRevision)),
            TestContext.Current.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _manifests.Add(manifest);
        }

        return response;
    }

    internal async Task<HttpResponseMessage> RebootstrapAsync(ScenarioMember signer, bool includeContinuity = false)
    {
        var previous = CurrentKey;
        var next = new ApproverGroupKey((previous?.KeyVersion ?? 0) + 1);
        var challenge = await ChallengeAsync(signer);
        var manifest = SignManifest(next, ulong.Parse(CurrentManifest?.ManifestRevision ?? "0") + 1, challenge,
            includeContinuity ? previous : null);
        var response = await signer.Client.PostAsJsonAsync(
            "api/approver-keys/rebootstrap",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest,
                Wraps(challenge.Approvers, next.KeyVersion, challenge.ApproverSetRevision)),
            TestContext.Current.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            CurrentKey = next;
            _manifests.Add(manifest);
        }

        return response;
    }

    internal async Task<HttpResponseMessage> RotateAsync(
        ScenarioMember signer, bool forgeContinuity = false, bool omitContinuity = false)
    {
        var previous = CurrentKey!;
        var next = new ApproverGroupKey(previous.KeyVersion + 1);
        var challenge = await ChallengeAsync(signer);
        var manifest = SignManifest(next, ulong.Parse(CurrentManifest!.ManifestRevision) + 1, challenge,
            omitContinuity ? null : forgeContinuity ? new ApproverGroupKey(previous.KeyVersion) : previous);
        var response = await signer.Client.PostAsJsonAsync(
            "api/approver-keys/rotate",
            new ApproverKeySetChangeRequest(challenge.ChallengeId, manifest,
                Wraps(challenge.Approvers, next.KeyVersion, challenge.ApproverSetRevision)),
            TestContext.Current.CancellationToken);
        if (response.IsSuccessStatusCode)
        {
            CurrentKey = next;
            _manifests.Add(manifest);
        }

        return response;
    }

    internal AgentEntrySubmissionContract NewSubmission(
        ScenarioAgent agent,
        Guid? submissionId = null,
        AgentSubmissionTargetContract? target = null,
        ushort submissionKind = 1,
        ushort entryType = 1,
        int payloadSeed = 7) =>
        Sign(agent, new AgentEntrySubmissionContract(
            SubmissionSchema,
            1,
            2,
            OrganizationId.ToString("D"),
            (submissionId ?? Guid.NewGuid()).ToString("D"),
            agent.Id.ToString("D"),
            submissionKind,
            entryType,
            target ?? new AgentSubmissionTargetContract("new"),
            "1",
            agent.AccessEpoch.ToString(),
            agent.KeyVersion,
            Encode(VaultKeyFingerprint.Compute(Convert.FromBase64String(agent.X25519PublicKey), VaultKeyKind.AgentX25519)),
            CurrentKey!.KeyVersion,
            Encode(CurrentKey.MessageKeyFingerprint),
            CurrentManifest!.ManifestRevision,
            Encode(Bytes(payloadSeed, 24 + 96 + 16)),
            Encode(Bytes(payloadSeed + 1, 120)),
            Encode(Bytes(payloadSeed + 2, 120)),
            string.Empty));

    internal AgentEntrySubmissionContract UpdateSubmission(ScenarioAgent agent, Guid entryId, ulong expectedRevision) =>
        NewSubmission(agent,
            target: new AgentSubmissionTargetContract("update", VaultId: VaultId.ToString("D"),
                EntryId: entryId.ToString("D"), ExpectedEntryRevision: expectedRevision.ToString()),
            submissionKind: 2);

    internal AgentEntrySubmissionContract Rewrap(ScenarioAgent agent, AgentEntrySubmissionContract stored) =>
        Sign(agent, stored with
        {
            SubmissionRevision = (ulong.Parse(stored.SubmissionRevision) + 1).ToString(),
            ApproverKeyVersion = CurrentKey!.KeyVersion,
            ApproverMessageKeyFingerprint = Encode(CurrentKey.MessageKeyFingerprint),
            ApproverManifestRevision = CurrentManifest!.ManifestRevision,
            EncodedAgentWrappedDek = Encode(RandomNumberGenerator.GetBytes(120)),
            EncodedApproverWrappedDek = Encode(RandomNumberGenerator.GetBytes(120)),
        });

    internal static AgentEntrySubmissionContract Sign(ScenarioAgent agent, AgentEntrySubmissionContract unsigned) =>
        unsigned with
        {
            AgentSignature = agent.Signing.SignBytesBase64Url(SignatureInput(
                "PLDNSUB1SIG:AGENT-SUBMISSION:", SubmissionMembers(unsigned))),
        };

    internal static Task<HttpResponseMessage> PostSubmissionAsync(ScenarioAgent agent, AgentEntrySubmissionContract submission) =>
        agent.Client.PostAsJsonAsync("api/agent/submissions", submission, TestContext.Current.CancellationToken);

    internal static Task<HttpResponseMessage> PutRewrapAsync(ScenarioAgent agent, AgentEntrySubmissionContract submission) =>
        agent.Client.PutAsJsonAsync(
            $"api/agent/submissions/{submission.SubmissionId}/wrap", submission, TestContext.Current.CancellationToken);

    internal async Task<Guid> SubmitAsync(ScenarioAgent agent, AgentEntrySubmissionContract submission)
    {
        var response = await PostSubmissionAsync(agent, submission);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return Guid.Parse(submission.SubmissionId);
    }

    internal async Task<Guid> ReserveEntryIdAsync(HttpClient client)
    {
        var (response, result) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest { VaultId = VaultId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return result!.Items.Single().EntryId;
    }

    internal async Task<Guid> CreateEntryAsync()
    {
        var entryId = await ReserveEntryIdAsync(Owner.Client);
        var (response, _) = await Owner.Client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(
                EntryEnvelopeFaker.CreateRequest(OrganizationId, VaultId, entryId));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return entryId;
    }

    internal async Task<Guid> GrantAsync(ScenarioAgent agent, Guid entryId, ulong entryRevision = 1)
    {
        var grantId = Guid.NewGuid();
        var (response, _) = await Owner.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(
                GrantRequest(agent, entryId, grantId, entryRevision));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return grantId;
    }

    internal CreateGranularGrantRequest GrantRequest(ScenarioAgent agent, Guid entryId, Guid grantId, ulong entryRevision = 1) => new()
    {
        GrantId = grantId,
        VaultId = VaultId,
        EntryId = entryId,
        AgentId = agent.Id,
        Methods = GrantMethods.Inject,
        GrantEntry = GrantEnvelopeTestData.Contract(
            OrganizationId, VaultId, grantId, entryId, agent.X25519PublicKey,
            entryRevision: entryRevision, agentId: agent.Id, methods: GrantMethods.Inject),
    };

    internal async Task SetApproverAsync(Guid userId, bool isApprover)
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await context.OrganizationApprovers
            .Where(x => x.OrganizationId == OrganizationId && x.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsApprover, isApprover));
    }

    internal async Task AddVaultMemberAsync(Guid userId)
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await context.Vaults
            .Include(x => x.VaultMembers)
            .Include(x => x.VaultMemberKeyEnvelopes)
            .SingleAsync(x => x.OrganizationId == OrganizationId && x.Id == VaultId);
        vault.AddMember(userId, VaultFaker.CreateMemberKey(vault.Scope, userId), _apiFactory.FakeClock.GetCurrentInstant());
        await context.SaveChangesAsync();
    }

    internal async Task<AgentEntrySubmission> LoadSubmissionAsync(Guid submissionId)
    {
        await using var scope = _apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        return await context.AgentEntrySubmissions.SingleAsync(x => x.OrganizationId == OrganizationId && x.Id == submissionId);
    }

    internal static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    internal static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);

    internal static byte[] SignatureInput(string domain, SortedDictionary<string, object> members)
    {
        var prefix = Encoding.ASCII.GetBytes(domain);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(members);
        var input = new byte[prefix.Length + 2 + canonical.Length];
        prefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(prefix.Length), 2);
        canonical.CopyTo(input, prefix.Length + 2);
        return input;
    }

    private static async Task<ScenarioMember> RegisterApproverAsync(ApiFactory apiFactory, User user, Guid organizationId)
    {
        var publicKey = RandomNumberGenerator.GetBytes(32);
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, publicKey);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            context.OrganizationApprovers.Add(OrganizationApprover.Create(
                organizationId, user.Id, true, SystemClock.Instance.GetCurrentInstant()));
            await context.SaveChangesAsync();
        }

        return new ScenarioMember(user, apiFactory.CreateAuthenticatedClient(user), publicKey);
    }

    private OrganizationApproverManifestContract SignManifest(
        ApproverGroupKey key,
        ulong manifestRevision,
        IssueApproverKeyChallengeResponse challenge,
        ApproverGroupKey? previous)
    {
        var unsigned = new OrganizationApproverManifestContract(
            ManifestSchema,
            1,
            2,
            OrganizationId.ToString("D"),
            key.KeyVersion,
            manifestRevision.ToString(),
            challenge.ApproverSetRevision,
            challenge.ApproverSetDigest,
            Encode(key.MessagePublicKey),
            Encode(key.MessageKeyFingerprint),
            Encode(key.SigningPublicKey),
            Encode(key.SigningKeyFingerprint),
            challenge.IssuedAt,
            string.Empty,
            previous is null ? null : Encode(previous.SigningKeyFingerprint));
        var members = ManifestMembers(unsigned);
        var signature = Encode(key.Sign(SignatureInput("PLDNSUB1SIG:APPROVER-MANIFEST:", members)));
        var continuity = previous is null
            ? null
            : Encode(previous.Sign(SignatureInput("PLDNSUB1SIG:APPROVER-CONTINUITY:", members)));
        return unsigned with { Signature = signature, ContinuitySignature = continuity };
    }

    private List<ApproverKeyWrapContract> Wraps(IEnumerable<ApproverContract> approvers, uint keyVersion, string approverSetRevision) =>
        approvers.SelectMany(approver => new ushort[] { 8, 9 }.Select(purpose => new ApproverKeyWrapContract(
            new ApproverKeyWrapDescriptorContract(
                purpose,
                33,
                OrganizationId.ToString("D"),
                approver.MemberId,
                approverSetRevision,
                keyVersion,
                5,
                approver.MemberKeyVersion,
                approver.MemberKeyFingerprint),
            Encode(RandomNumberGenerator.GetBytes(120)))))
            .ToList();

    private static SortedDictionary<string, object> ManifestMembers(OrganizationApproverManifestContract manifest)
    {
        var members = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = manifest.Schema,
            ["contractVersion"] = manifest.ContractVersion,
            ["protocolVersion"] = manifest.ProtocolVersion,
            ["organizationId"] = manifest.OrganizationId,
            ["keyVersion"] = manifest.KeyVersion,
            ["manifestRevision"] = manifest.ManifestRevision,
            ["approverSetRevision"] = manifest.ApproverSetRevision,
            ["approverSetDigest"] = manifest.ApproverSetDigest,
            ["messagePublicKey"] = manifest.MessagePublicKey,
            ["messageKeyFingerprint"] = manifest.MessageKeyFingerprint,
            ["signingPublicKey"] = manifest.SigningPublicKey,
            ["signingKeyFingerprint"] = manifest.SigningKeyFingerprint,
            ["issuedAt"] = manifest.IssuedAt,
        };
        if (manifest.PreviousSigningKeyFingerprint is not null)
        {
            members["previousSigningKeyFingerprint"] = manifest.PreviousSigningKeyFingerprint;
        }

        return members;
    }

    private static SortedDictionary<string, object> SubmissionMembers(AgentEntrySubmissionContract submission)
    {
        var target = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["kind"] = submission.Target.Kind };
        if (submission.Target.SuggestedVaultId is not null) target["suggestedVaultId"] = submission.Target.SuggestedVaultId;
        if (submission.Target.VaultId is not null) target["vaultId"] = submission.Target.VaultId;
        if (submission.Target.EntryId is not null) target["entryId"] = submission.Target.EntryId;
        if (submission.Target.ExpectedEntryRevision is not null) target["expectedEntryRevision"] = submission.Target.ExpectedEntryRevision;
        return new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = submission.Schema,
            ["contractVersion"] = submission.ContractVersion,
            ["protocolVersion"] = submission.ProtocolVersion,
            ["organizationId"] = submission.OrganizationId,
            ["submissionId"] = submission.SubmissionId,
            ["agentId"] = submission.AgentId,
            ["submissionKind"] = submission.SubmissionKind,
            ["entryType"] = submission.EntryType,
            ["target"] = target,
            ["submissionRevision"] = submission.SubmissionRevision,
            ["agentAccessEpoch"] = submission.AgentAccessEpoch,
            ["agentKeyVersion"] = submission.AgentKeyVersion,
            ["agentKeyFingerprint"] = submission.AgentKeyFingerprint,
            ["approverKeyVersion"] = submission.ApproverKeyVersion,
            ["approverMessageKeyFingerprint"] = submission.ApproverMessageKeyFingerprint,
            ["approverManifestRevision"] = submission.ApproverManifestRevision,
            ["encodedPayload"] = submission.EncodedPayload,
            ["encodedAgentWrappedDek"] = submission.EncodedAgentWrappedDek,
            ["encodedApproverWrappedDek"] = submission.EncodedApproverWrappedDek,
        };
    }

    private static byte[] Bytes(int seed, int count) =>
        Enumerable.Range(0, count).Select(i => (byte)((i * 31) + seed)).ToArray();
}
