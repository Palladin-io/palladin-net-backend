using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSec.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
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
using AgentStatus = Palladin.Core.Types.AgentStatus;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class AgentDiscoveryProvisioningTests(ApiFactory apiFactory) : TestBase
{
    private static readonly byte[] SignaturePrefix = Encoding.ASCII.GetBytes("PLDNV2SIG:VAULT-MANIFEST:");
    private static readonly byte[] WrappedVdkDigestPrefix = Encoding.ASCII.GetBytes("PLDNV2DG:AGENT-WRAPPED-VDK:");

    [Fact]
    public async Task When_MemberProvisionsValidSignedManifest_Then_PersistsOneOpaqueEnvelopeIdempotently()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var request = CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var first = await client.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(request);
        var retry = await client.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(request);

        // Then
        first.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.AgentVaultDiscoveryEnvelopes
            .Where(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
        persisted.AgentWrappedVdk.ShouldNotBeEmpty();
        persisted.ManifestSignature.Length.ShouldBe(64);
        persisted.ManifestRevision.Value.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_ManifestSignatureIsTampered_Then_RejectsWithoutPersistingEnvelope()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var request = CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1);
        var changedPrefix = request.Manifest.Signature[0] == 'A' ? 'B' : 'A';
        var changedSignature = $"{changedPrefix}{request.Manifest.Signature[1..]}";
        var tampered = request with
        {
            Envelope = request.Envelope with { ManifestSignature = changedSignature },
            Manifest = request.Manifest with { Signature = changedSignature },
        };
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(tampered);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.AgentVaultDiscoveryEnvelopes.AnyAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_PersistedManifestIsCoherentlyResignedWithUntrustedKey_Then_ListAndSyncFailClosed()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var requestSigning = AgentRequestSigning.Generate();
        var agentKeys = new AgentKeys(AgentFaker.GeneratePublicKey(), requestSigning.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: agentKeys.X25519PublicKey,
                    signingPublicKey: agentKeys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var provisioning = CreateRequest(organization.Id, vault.Id, sourceAgent.Id, agentKeys, 1);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(provisioning))
            .EnsureSuccessStatusCode();

        using var substitutedSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var substitutedPublicKey = substitutedSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var substitutedFingerprint = VaultKeyFingerprint.Compute(
            substitutedPublicKey,
            VaultKeyKind.VaultSigningEd25519);
        var substitutedManifest = provisioning.Manifest with
        {
            VaultSigningPublicKey = Encode(substitutedPublicKey),
            VaultSigningKeyFingerprint = Encode(substitutedFingerprint),
            Signature = string.Empty,
        };
        var substitutedSignature = SignatureAlgorithm.Ed25519.Sign(
            substitutedSigningKey,
            CreateSignatureInput(substitutedManifest));

        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = mutationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.AgentVaultDiscoveryEnvelopes
                .Where(x => x.OrganizationId == organization.Id
                            && x.VaultId == vault.Id
                            && x.AgentId == sourceAgent.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.VaultSigningPublicKey, substitutedPublicKey)
                        .SetProperty(x => x.VaultSigningKeyFingerprint, substitutedFingerprint)
                        .SetProperty(x => x.ManifestSignature, substitutedSignature),
                    TestContext.Current.CancellationToken);
        }

        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            agentKeys.X25519PublicKey,
            requestSigning);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "1");

        // When
        var listResponse = await agentClient.GetAsync(
            "api/agent/vault-manifests",
            TestContext.Current.CancellationToken);
        var syncResponse = await agentClient.PostAsJsonAsync(
            $"api/agent/vaults/{vault.Id}/discovery/sync/snapshot",
            new GetAgentDiscoverySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);

        // Then
        listResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        syncResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(provisioning.Envelope.AgentWrappedVdk, Case.Sensitive);
        (await syncResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(provisioning.Envelope.AgentWrappedVdk, Case.Sensitive);
    }

    [Fact]
    public async Task When_ManifestIssuedAtExceedsMicrosecondPrecision_Then_RejectsWithoutPersistingEnvelope()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var issuedAt = TruncateToMicroseconds(SystemClock.Instance.GetCurrentInstant()) + Duration.FromTicks(1);
        var request = CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1, issuedAt);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.AgentVaultDiscoveryEnvelopes.AnyAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_MemberListsDiscoveryProvisioning_Then_ReturnsEveryActiveAgentAsCurrentOrPending()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var currentKeys = CreateAgentKeys();
        var current = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: currentKeys.X25519PublicKey,
            signingPublicKey: currentKeys.Ed25519PublicKey);
        var pending = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        await apiFactory.Services.SeedVaultAgentAsync(organization.Id, status: AgentStatus.Deactivated);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var request = CreateRequest(organization.Id, vault.Id, current.Id, currentKeys, 1);
        (await client.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(request))
            .EnsureSuccessStatusCode();

        // When
        var (_, response) = await client
            .GETAsync<ListAgentDiscoveryProvisioningEndpoint, ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>(
                new ListAgentDiscoveryProvisioningRequest { VaultId = vault.Id });

        // Then
        response.ShouldNotBeNull();
        response.Items.Count.ShouldBe(2);
        response.Items.Single(x => x.AgentId == current.Id).Status.ShouldBe("current");
        response.Items.Single(x => x.AgentId == current.Id).AgentName.ShouldBe(current.Name);
        response.Items.Single(x => x.AgentId == pending.Id).Status.ShouldBe("pending");
        response.NextAfterId.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentCatalogSpansPages_Then_KeysetCursorReturnsEveryAgentExactlyOnce()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var expectedIds = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            expectedIds.Add((await apiFactory.Services.SeedVaultAgentAsync(organization.Id)).Id);
        }
        expectedIds.Sort();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var actualIds = new List<Guid>();
        Guid? cursor = null;
        do
        {
            var (_, page) = await client
                .GETAsync<ListAgentDiscoveryProvisioningEndpoint, ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>(
                    new ListAgentDiscoveryProvisioningRequest
                    {
                        VaultId = vault.Id,
                        AfterId = cursor,
                        PageSize = 2,
                    });
            page.ShouldNotBeNull();
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            page.Items.Select(x => x.AgentId).ShouldBeInOrder();
            actualIds.AddRange(page.Items.Select(x => x.AgentId));
            cursor = page.NextAfterId;
        } while (cursor is not null);

        // Then
        actualIds.ShouldBe(expectedIds);
        actualIds.Distinct().Count().ShouldBe(expectedIds.Count);
    }

    [Fact]
    public async Task When_AuthenticatedActiveAgentRequestsCurrentProtocol_Then_ReturnsOnlyValidCurrentManifests()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var requestSigning = AgentRequestSigning.Generate();
        var agentKeys = new AgentKeys(AgentFaker.GeneratePublicKey(), requestSigning.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
                AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: agentKeys.X25519PublicKey,
                    signingPublicKey: agentKeys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        var provisioning = CreateRequest(organization.Id, vault.Id, sourceAgent.Id, agentKeys, 1);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(provisioning))
            .EnsureSuccessStatusCode();
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            agentKeys.X25519PublicKey,
            requestSigning);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        // When
        var (_, response) = await agentClient
            .GETAsync<ListAgentVaultManifestsEndpoint, ListAgentVaultManifestsResponse>();

        // Then
        response.ShouldNotBeNull();
        response.AgentAccessEpoch.ShouldBe(sourceAgent.AccessEpoch);
        response.Items.Count.ShouldBe(1);
        response.Items[0].Envelope.VaultId.ShouldBe(vault.Id);
        response.Items[0].Envelope.AgentId.ShouldBe(sourceAgent.Id);
        response.Items[0].Manifest.Signature.ShouldBe(provisioning.Manifest.Signature);

        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = mutationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.AgentVaultDiscoveryEnvelopes
                .Where(x => x.OrganizationId == organization.Id
                            && x.VaultId == vault.Id
                            && x.AgentId == sourceAgent.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        x => x.ManifestSignature,
                        RandomNumberGenerator.GetBytes(64)),
                    TestContext.Current.CancellationToken);
        }

        var mutatedResponse = await agentClient.GetAsync(
            "api/agent/vault-manifests",
            TestContext.Current.CancellationToken);
        mutatedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await mutatedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(provisioning.Envelope.AgentWrappedVdk, Case.Sensitive);
    }

    [Fact]
    public async Task When_ActiveAgentHasProvisionedDiscovery_Then_ListsManifestWithoutSeparatePairing()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var signing = AgentRequestSigning.Generate();
        var keys = new AgentKeys(AgentFaker.GeneratePublicKey(), signing.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: keys.X25519PublicKey,
                    signingPublicKey: keys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: keys.X25519PublicKey,
            signingPublicKey: keys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, sourceAgent.Id, keys, 1)))
            .EnsureSuccessStatusCode();
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            keys.X25519PublicKey,
            signing);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");

        var (_, response) = await agentClient
            .GETAsync<ListAgentVaultManifestsEndpoint, ListAgentVaultManifestsResponse>();

        response.ShouldNotBeNull();
        response.AgentAccessEpoch.ShouldBe(sourceAgent.AccessEpoch);
        response.Items.Count.ShouldBe(1);
        response.Items[0].Envelope.AgentId.ShouldBe(sourceAgent.Id);
        response.Items[0].Envelope.VaultId.ShouldBe(vault.Id);
    }

    [Fact]
    public async Task When_NewVaultIsProvisionedForActiveAgent_Then_ListsItsManifestWithoutSeparatePairing()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var firstVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var signing = AgentRequestSigning.Generate();
        var keys = new AgentKeys(AgentFaker.GeneratePublicKey(), signing.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: keys.X25519PublicKey,
                    signingPublicKey: keys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: keys.X25519PublicKey,
            signingPublicKey: keys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, firstVault.Id, sourceAgent.Id, keys, 1)))
            .EnsureSuccessStatusCode();
        var newVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);

        var (_, pending) = await memberClient
            .GETAsync<ListAgentDiscoveryProvisioningEndpoint, ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>(
                new ListAgentDiscoveryProvisioningRequest { VaultId = newVault.Id });
        pending.ShouldNotBeNull();
        pending.Items.Single(x => x.AgentId == sourceAgent.Id).Status.ShouldBe("pending");

        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, newVault.Id, sourceAgent.Id, keys, 1)))
            .EnsureSuccessStatusCode();
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            keys.X25519PublicKey,
            signing);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");

        // When
        var (_, response) = await agentClient
            .GETAsync<ListAgentVaultManifestsEndpoint, ListAgentVaultManifestsResponse>();

        // Then
        response.ShouldNotBeNull();
        response.AgentAccessEpoch.ShouldBe(sourceAgent.AccessEpoch);
        response.Items.Select(x => x.Envelope.VaultId).ShouldBe([firstVault.Id, newVault.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task When_DifferentOrganizationAttemptsPairingConfirmation_Then_FailsClosed()
    {
        var (member, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var signing = AgentRequestSigning.Generate();
        var keys = new AgentKeys(AgentFaker.GeneratePublicKey(), signing.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: keys.X25519PublicKey,
                    signingPublicKey: keys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: keys.X25519PublicKey,
            signingPublicKey: keys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(member);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, sourceAgent.Id, keys, 1)))
            .EnsureSuccessStatusCode();
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            keys.X25519PublicKey,
            signing);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        var activationId = Guid.NewGuid();
        var (_, activation) = await agentClient
            .POSTAsync<CreateAgentPairingActivationEndpoint, CreateAgentPairingActivationRequest, AgentPairingActivationResponse>(
                new CreateAgentPairingActivationRequest(activationId));
        activation.ShouldNotBeNull();
        var digest = ComputePairingDigest(activation);
        var (outsider, _, _) = await apiFactory.Services.SeedUserAsync();
        var outsiderClient = apiFactory.CreateAuthenticatedClient(outsider);

        var response = await outsiderClient.PostAsJsonAsync(
            $"api/agents/{sourceAgent.Id}/pairing/activations/{activationId}/confirm",
            new ConfirmAgentPairingActivationRequest(digest),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_AuthenticatedEpochAdvancesBeforeVaultReplica_Then_DoesNotServePriorEpochManifest()
    {
        // Given: the source Agent is already in epoch 2, while Vault still has its epoch-1 replica and envelope.
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var requestSigning = AgentRequestSigning.Generate();
        var agentKeys = new AgentKeys(AgentFaker.GeneratePublicKey(), requestSigning.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: agentKeys.X25519PublicKey,
                    signingPublicKey: agentKeys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active)
                .RuleFor(x => x.AccessEpoch, 2u)
                .RuleFor(x => x.ReactivatedAt, apiFactory.FakeClock.GetCurrentInstant()));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, sourceAgent.Id, agentKeys, 1)))
            .EnsureSuccessStatusCode();
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            agentKeys.X25519PublicKey,
            requestSigning);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");

        // When
        var response = await agentClient.GetAsync(
            "api/agent/vault-manifests",
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("agentWrappedVdk", Case.Insensitive);
    }

    [Fact]
    public async Task When_AgentReactivatesBeforeDelayedDeactivation_Then_DoesNotServePriorEpochManifest()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var requestSigning = AgentRequestSigning.Generate();
        var agentKeys = new AgentKeys(AgentFaker.GeneratePublicKey(), requestSigning.PublicKeyBase64);
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: agentKeys.X25519PublicKey,
                    signingPublicKey: agentKeys.Ed25519PublicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));
        var replica = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: sourceAgent.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, sourceAgent.Id, agentKeys, 1)))
            .EnsureSuccessStatusCode();
        var reactivatedAt = replica.UpdatedAt + Duration.FromMilliseconds(1);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentUpserted(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentUpsertedEvent(
                sourceAgent.Id,
                organization.Id,
                AgentStatus.Active,
                agentKeys.X25519PublicKey,
                replica.RecipientKeyVersion,
                agentKeys.Ed25519PublicKey,
                sourceAgent.Name,
                sourceAgent.Type,
                sourceAgent.IconKey,
                sourceAgent.IconColor,
                2,
                reactivatedAt,
                reactivatedAt)));
        }
        var agentClient = apiFactory.CreateSignedAgentClient(
            sourceAgent.Id,
            apiKey,
            agentKeys.X25519PublicKey,
            requestSigning);
        agentClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");

        // When
        var response = await agentClient.GetAsync(
            "api/agent/vault-manifests",
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("agentWrappedVdk", Case.Insensitive);

        var (_, provisioningResponse) = await memberClient
            .GETAsync<ListAgentDiscoveryProvisioningEndpoint, ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>(
                new ListAgentDiscoveryProvisioningRequest { VaultId = vault.Id });
        provisioningResponse.ShouldNotBeNull();
        provisioningResponse.Items.Single(x => x.AgentId == sourceAgent.Id).Status.ShouldBe("pending");
    }

    [Fact]
    public async Task When_AuthenticatedAgentOmitsVaultProtocol_Then_RequiresExplicitProtocolUpgrade()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        var requestSigning = AgentRequestSigning.Generate();
        var publicKey = AgentFaker.GeneratePublicKey();
        var sourceAgent = await apiFactory.Services.SeedAgentAsync(
            organizationId,
            AgentFaker.Create(
                    organizationId: organizationId,
                    publicKey: publicKey,
                    signingPublicKey: requestSigning.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organizationId,
            id: sourceAgent.Id,
            publicKey: publicKey,
            signingPublicKey: requestSigning.PublicKeyBase64);
        var client = apiFactory.CreateSignedAgentClient(sourceAgent.Id, apiKey, publicKey, requestSigning);

        // When
        var response = await client.GetAsync(
            "api/agent/vault-manifests",
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe((HttpStatusCode)426);
    }

    [Fact]
    public async Task When_AgentIsDeactivated_Then_RetainsRevisionFloorButDoesNotServeDiscovery()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var now = TruncateToMicroseconds(apiFactory.FakeClock.GetCurrentInstant());
        var agentUpdatedAt = now - Duration.FromMilliseconds(2);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey,
            updatedAt: agentUpdatedAt);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        var provisioning = CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(provisioning))
            .EnsureSuccessStatusCode();
        // The source deactivation happened before provisioning but its event arrived afterwards.
        var deactivatedAt = agentUpdatedAt + Duration.FromMilliseconds(1);
        var inactiveReplicaAt = now + Duration.FromMilliseconds(1);

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentUpserted(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentUpsertedEvent(
                agent.Id,
                organization.Id,
                AgentStatus.Deactivated,
                agent.PublicKey,
                agent.RecipientKeyVersion,
                agent.SigningPublicKey,
                agent.Name,
                "runtime",
                agent.IconKey,
                agent.IconColor,
                1,
                null,
                inactiveReplicaAt)));
        }

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentDeactivated(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>());
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentDeactivatedEvent(
                agent.Id,
                organization.Id,
                user.Id,
                "Operator",
                "Agent",
                1,
                deactivatedAt)));
        }

        // Then
        var replay = await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(provisioning);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var envelope = await readContext.AgentVaultDiscoveryEnvelopes.SingleAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
            TestContext.Current.CancellationToken);
        envelope.RevokedAt.ShouldNotBeNull();
        var persistedAgent = await readContext.Agents.SingleAsync(
            x => x.OrganizationId == organization.Id && x.Id == agent.Id,
            TestContext.Current.CancellationToken);
        persistedAgent.Status.ShouldBe(AgentStatus.Deactivated);
        persistedAgent.UpdatedAt.ShouldBe(inactiveReplicaAt);

        var (_, response) = await memberClient
            .GETAsync<ListAgentDiscoveryProvisioningEndpoint, ListAgentDiscoveryProvisioningRequest, ListAgentDiscoveryProvisioningResponse>(
                new ListAgentDiscoveryProvisioningRequest { VaultId = vault.Id });
        response.ShouldNotBeNull();
        response.Items.ShouldNotContain(x => x.AgentId == agent.Id);
    }

    [Fact]
    public async Task When_DeactivationArrivesAfterReactivation_Then_TombstonesOnceWithoutRollingBackReplica()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var now = TruncateToMicroseconds(apiFactory.FakeClock.GetCurrentInstant());
        var t0 = now - Duration.FromMilliseconds(3);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey,
            updatedAt: t0);
        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1)))
            .EnsureSuccessStatusCode();
        var deactivatedAt = t0 + Duration.FromMilliseconds(1);
        var reactivatedAt = now + Duration.FromMilliseconds(1);
        var deactivation = new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            1,
            deactivatedAt);

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentUpserted(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentUpsertedEvent(
                agent.Id,
                organization.Id,
                AgentStatus.Active,
                agent.PublicKey,
                agent.RecipientKeyVersion,
                agent.SigningPublicKey,
                agent.Name,
                "runtime",
                agent.IconKey,
                agent.IconColor,
                2,
                reactivatedAt,
                reactivatedAt)));
        }

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentDeactivated(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>());
            await consumer.Consume(apiFactory.MockConsumeContext(deactivation));
        }

        // Then
        await using (var verifyScope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = verifyScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var replica = await readContext.Agents.SingleAsync(
                x => x.OrganizationId == organization.Id && x.Id == agent.Id,
                TestContext.Current.CancellationToken);
            replica.Status.ShouldBe(AgentStatus.Active);
            replica.UpdatedAt.ShouldBe(reactivatedAt);
            replica.AccessEpoch.ShouldBe(2u);
            replica.LastProcessedDeactivationEpoch.ShouldBe(1u);
            replica.LastProcessedDeactivationAt.ShouldBe(deactivatedAt);
            var revoked = await readContext.AgentVaultDiscoveryEnvelopes.SingleAsync(
                x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
                TestContext.Current.CancellationToken);
            revoked.RevokedAt.ShouldBe(deactivatedAt);
        }

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentDeactivated(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>());
            await consumer.Consume(apiFactory.MockConsumeContext(deactivation));
        }

        await using var retryScope = apiFactory.Services.CreateAsyncScope();
        var retryReadContext = retryScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replayed = await retryReadContext.AgentVaultDiscoveryEnvelopes.SingleAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
            TestContext.Current.CancellationToken);
        replayed.ManifestRevision.Value.ShouldBe(1UL);
        replayed.RevokedAt.ShouldBe(deactivatedAt);
    }

    [Fact]
    public async Task When_ReprovisionedAfterReactivationBeforeDelayedDeactivation_Then_PreservesCurrentManifest()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentKeys = CreateAgentKeys();
        var t0 = TruncateToMicroseconds(apiFactory.FakeClock.GetCurrentInstant())
                 - Duration.FromMilliseconds(3);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            publicKey: agentKeys.X25519PublicKey,
            signingPublicKey: agentKeys.Ed25519PublicKey,
            updatedAt: t0);
        var deactivatedAt = t0 + Duration.FromMilliseconds(1);
        var reactivatedAt = t0 + Duration.FromMilliseconds(2);

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentUpserted(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
            await consumer.Consume(apiFactory.MockConsumeContext(new AgentUpsertedEvent(
                agent.Id,
                organization.Id,
                AgentStatus.Active,
                agent.PublicKey,
                agent.RecipientKeyVersion,
                agent.SigningPublicKey,
                agent.Name,
                "runtime",
                agent.IconKey,
                agent.IconColor,
                2,
                reactivatedAt,
                reactivatedAt)));
        }

        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        (await memberClient.PUTAsync<ProvisionAgentDiscoveryEndpoint, ProvisionAgentDiscoveryRequest>(
            CreateRequest(organization.Id, vault.Id, agent.Id, agentKeys, 1)))
            .EnsureSuccessStatusCode();
        var deactivation = new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            1,
            deactivatedAt);

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = new OnAgentDeactivated(
                scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                scope.ServiceProvider.GetRequiredService<IClock>());
            await consumer.Consume(apiFactory.MockConsumeContext(deactivation));
        }

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Agents.SingleAsync(
            x => x.OrganizationId == organization.Id && x.Id == agent.Id,
            TestContext.Current.CancellationToken);
        replica.Status.ShouldBe(AgentStatus.Active);
        replica.AccessEpoch.ShouldBe(2u);
        replica.LastProcessedDeactivationEpoch.ShouldBe(1u);
        replica.UpdatedAt.ShouldBe(reactivatedAt);
        replica.LastProcessedDeactivationAt.ShouldBe(deactivatedAt);
        var current = await readContext.AgentVaultDiscoveryEnvelopes.SingleAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.AgentId == agent.Id,
            TestContext.Current.CancellationToken);
        current.ManifestRevision.Value.ShouldBe(1UL);
        current.ProvisionedAccessEpoch.ShouldBe(2u);
        current.ProvisionedAt.ShouldBeGreaterThanOrEqualTo(reactivatedAt);
        current.RevokedAt.ShouldBeNull();
    }

    private static AgentKeys CreateAgentKeys() =>
        new(AgentFaker.GeneratePublicKey(), AgentRequestSigning.Generate().PublicKeyBase64);

    private static string ComputePairingDigest(AgentPairingActivationResponse activation) =>
        WebEncoders.Base64UrlEncode(AgentPairingTranscriptService.ComputeDigest(
            activation.ActivationId,
            activation.OrganizationId,
            activation.AgentId,
            WebEncoders.Base64UrlDecode(activation.AgentX25519Fingerprint),
            WebEncoders.Base64UrlDecode(activation.AgentEd25519Fingerprint),
            activation.CandidateManifests));

    private static ProvisionAgentDiscoveryRequest CreateRequest(
        Guid organizationId,
        Guid vaultId,
        Guid agentId,
        AgentKeys agentKeys,
        ulong revision,
        Instant? issuedAt = null)
    {
        var agentX25519Key = Convert.FromBase64String(agentKeys.X25519PublicKey);
        var agentEd25519Key = Convert.FromBase64String(agentKeys.Ed25519PublicKey);
        var wrappedVdk = RandomNumberGenerator.GetBytes(120);
        using var vaultSigningKey = VaultTrustAnchorFaker.CreateManifestSigningKey();
        var vaultSigningPublicKey = vaultSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var vaultMessagePublicKey = VaultTrustAnchorFaker.AgentMessagePublicKey;
        var manifest = new VaultManifestContract(
            2,
            1,
            organizationId,
            vaultId,
            agentId,
            Encode(VaultKeyFingerprint.Compute(agentX25519Key, VaultKeyKind.AgentX25519)),
            Encode(VaultKeyFingerprint.Compute(agentEd25519Key, VaultKeyKind.AgentEd25519)),
            Encode(vaultSigningPublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultSigningPublicKey, VaultKeyKind.VaultSigningEd25519)),
            1,
            Encode(vaultMessagePublicKey),
            Encode(VaultKeyFingerprint.Compute(vaultMessagePublicKey, VaultKeyKind.VaultMessageX25519)),
            1,
            1,
            Encode(ComputeWrappedVdkDigest(wrappedVdk)),
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            issuedAt ?? TruncateToMicroseconds(SystemClock.Instance.GetCurrentInstant()),
            2,
            string.Empty);
        var signature = Encode(SignatureAlgorithm.Ed25519.Sign(vaultSigningKey, CreateSignatureInput(manifest)));
        manifest = manifest with { Signature = signature };
        var envelope = new AgentVaultDiscoveryEnvelopeContract(
            2,
            organizationId,
            vaultId,
            agentId,
            1,
            1,
            1,
            Encode(VaultKeyFingerprint.Compute(agentX25519Key, VaultKeyKind.AgentX25519)),
            Encode(wrappedVdk),
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            signature);
        return new ProvisionAgentDiscoveryRequest
        {
            VaultId = vaultId,
            AgentId = agentId,
            Envelope = envelope,
            Manifest = manifest,
        };
    }

    private static byte[] CreateSignatureInput(VaultManifestContract manifest)
    {
        var canonical = VaultManifestCryptoValidator.CanonicalizeUnsigned(manifest);
        var input = new byte[SignaturePrefix.Length + sizeof(ushort) + canonical.Length];
        SignaturePrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(SignaturePrefix.Length), manifest.ProtocolVersion);
        canonical.CopyTo(input, SignaturePrefix.Length + sizeof(ushort));
        return input;
    }

    private static byte[] ComputeWrappedVdkDigest(byte[] wrappedVdk)
    {
        var input = new byte[WrappedVdkDigestPrefix.Length + sizeof(ushort) + wrappedVdk.Length];
        WrappedVdkDigestPrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(WrappedVdkDigestPrefix.Length), 2);
        wrappedVdk.CopyTo(input, WrappedVdkDigestPrefix.Length + sizeof(ushort));
        return SHA256.HashData(input);
    }

    private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);

    private static Instant TruncateToMicroseconds(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - (ticks % 10));
    }

    private sealed record AgentKeys(string X25519PublicKey, string Ed25519PublicKey);
}
