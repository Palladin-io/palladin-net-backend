using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class VaultSyncTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_VaultRotationHoldsTheWriteFence_Then_MemberSnapshotWaitsBeforeReadingWrappers()
    {
        // Given
        var ct = TestContext.Current.CancellationToken;
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        await using var lockScope = apiFactory.Services.CreateAsyncScope();
        var writeContext = lockScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        await writeContext.LockVault(organization.Id, vault.Id).SingleAsync(ct);

        // When
        var snapshotTask = client.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest,
            MemberSnapshotResponse>(new GetMemberSnapshotRequest { VaultId = vault.Id });
        var completedBeforeRotationFence = await Task.WhenAny(snapshotTask, Task.Delay(100, ct)) == snapshotTask;
        await transaction.CommitAsync(ct);
        var (response, snapshot) = await snapshotTask;

        // Then
        completedBeforeRotationFence.ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        snapshot!.Items.Single().EntryKey.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_DiscoverySyncAuthorizationIsRead_Then_RevocationDoesNotWaitForResponseDelivery()
    {
        // Given
        var ct = TestContext.Current.CancellationToken;
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agentId,
            publicKey: provisioning.X25519PublicKey,
            signingPublicKey: provisioning.RequestSigning.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(provisioning.Request, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            seed: 0);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("agent_id", agent.Id.ToString()),
            new Claim("agent_organization_id", organization.Id.ToString()),
            new Claim("agent_access_epoch", agent.AccessEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ], "agent-test"));

        await using var syncScope = apiFactory.Services.CreateAsyncScope();
        var readContext = syncScope.ServiceProvider.GetRequiredService<VaultDomainReadContext>();
        var authorization = await AgentVaultSyncAuthorizer.AcquireAsync(
            principal,
            vault.Id,
            readContext,
            ct);
        authorization.ShouldNotBeNull();
        var deactivation = DeactivateAgentAsync(new AgentDeactivatedEvent(
            agent.Id,
            organization.Id,
            user.Id,
            "Operator",
            "Agent",
            agent.AccessEpoch,
            agent.UpdatedAt + Duration.FromMilliseconds(1)));
        var envelopeRevocation = RevokeEnvelopeAsync(
            organization.Id,
            vault.Id,
            agent.Id,
            agent.UpdatedAt + Duration.FromMilliseconds(1));

        // When
        var deactivationCompletedBeforeCiphertextRead = await Task.WhenAny(
            deactivation,
            Task.Delay(100, ct)) == deactivation;
        var envelopeRevocationCompletedBeforeCiphertextRead = await Task.WhenAny(
            envelopeRevocation,
            Task.Delay(100, ct)) == envelopeRevocation;
        var encryptedHead = await readContext.Entries.SingleAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.Id == entryId,
            ct);
        await Task.WhenAll(deactivation, envelopeRevocation);

        // Then
        deactivationCompletedBeforeCiphertextRead.ShouldBeTrue();
        envelopeRevocationCompletedBeforeCiphertextRead.ShouldBeTrue();
        encryptedHead.AgentDiscoveryEncodedSuitePayload.ShouldNotBeEmpty();
        (await AgentVaultSyncAuthorizer.IsCurrentAsync(authorization, readContext, ct)).ShouldBeFalse();
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<VaultDomainReadContext>();
        var deniedLease = await AgentVaultSyncAuthorizer.AcquireAsync(principal, vault.Id, verifyContext, ct);
        deniedLease.ShouldBeNull();
    }

    [Fact]
    public async Task When_ProvisionedActiveAgentSyncsDiscovery_Then_PrivateEntriesStayHiddenAndRemovalIsATombstone()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    id: agentId,
                    organizationId: organization.Id,
                    publicKey: provisioning.X25519PublicKey,
                    signingPublicKey: provisioning.RequestSigning.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agentId,
            publicKey: provisioning.X25519PublicKey,
            signingPublicKey: provisioning.RequestSigning.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(provisioning.Request, user.Id);
        var discoverableEntry = await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            seed: 0);
        await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            includeDiscovery: false,
            seed: 32);
        var agentClient = apiFactory.CreateSignedAgentClient(
            agentId,
            apiKey,
            provisioning.X25519PublicKey,
            provisioning.RequestSigning);
        AddSyncHeaders(agentClient);

        // When
        var (snapshotResponse, snapshot) = await agentClient.POSTAsync<
            GetAgentDiscoverySnapshotEndpoint,
            GetAgentDiscoverySnapshotRequest,
            AgentDiscoverySnapshotResponse>(new GetAgentDiscoverySnapshotRequest { VaultId = vault.Id });
        await apiFactory.Services.SeedSyncEntryUpdateAsync(
            organization.Id,
            vault.Id,
            discoverableEntry,
            user.Id,
            baseRevision: 1,
            disableDiscovery: true,
            seed: 64);
        var (deltaResponse, delta) = await agentClient.POSTAsync<
            GetAgentDiscoveryDeltaEndpoint,
            GetAgentDiscoveryDeltaRequest,
            AgentDiscoveryDeltaResponse>(new GetAgentDiscoveryDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = snapshot!.SnapshotBaseSequence,
            });

        // Then
        snapshotResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        snapshot.Items.Count.ShouldBe(1);
        snapshot.Items[0].EntryId.ShouldBe(discoverableEntry);
        snapshot.Items[0].Kind.ShouldBe("head");
        snapshot.Items[0].AgentDiscovery.ShouldNotBeNull();
        deltaResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        delta!.DeltaUpperBound.ShouldBe("2");
        delta.AppliedThroughSequence.ShouldBe("2");
        delta.Items.Count.ShouldBe(1);
        delta.Items[0].EntryId.ShouldBe(discoverableEntry);
        delta.Items[0].Kind.ShouldBe("tombstone");
        delta.Items[0].AgentDiscovery.ShouldBeNull();
    }

    [Fact]
    public async Task When_ProvisionedManifestIsTampered_Then_DiscoverySyncFailsClosedWithoutCiphertext()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    id: agentId,
                    organizationId: organization.Id,
                    publicKey: provisioning.X25519PublicKey,
                    signingPublicKey: provisioning.RequestSigning.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agentId,
            publicKey: provisioning.X25519PublicKey,
            signingPublicKey: provisioning.RequestSigning.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(provisioning.Request, user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = mutationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.AgentVaultDiscoveryEnvelopes
                .Where(x => x.OrganizationId == organization.Id
                            && x.VaultId == vault.Id
                            && x.AgentId == agentId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        x => x.ManifestSignature,
                        RandomNumberGenerator.GetBytes(64)),
                    TestContext.Current.CancellationToken);
        }

        var agentClient = apiFactory.CreateSignedAgentClient(
            agentId,
            apiKey,
            provisioning.X25519PublicKey,
            provisioning.RequestSigning);
        AddSyncHeaders(agentClient);

        // When
        var response = await agentClient.PostAsJsonAsync(
            $"api/agent/vaults/{vault.Id}/discovery/sync/snapshot",
            new GetAgentDiscoverySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("agentDiscovery", Case.Insensitive);
    }

    [Fact]
    public async Task When_MemberClosesSnapshotWithDelta_Then_RacingChangesAreRecoverableAndRetriesAreIdempotent()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        var entryIds = new List<Guid>
        {
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0),
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 32),
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 64),
        };
        var snapshotUpdatedAt = await LoadEntryUpdatedAtAsync(organization.Id, vault.Id, entryIds);

        // When
        var (_, firstSnapshot) = await client.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest,
            MemberSnapshotResponse>(new GetMemberSnapshotRequest
            {
                VaultId = vault.Id,
                PageSize = 2,
            });
        var (_, finalSnapshot) = await client.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest,
            MemberSnapshotResponse>(new GetMemberSnapshotRequest
            {
                VaultId = vault.Id,
                Cursor = firstSnapshot!.NextCursor,
                PageSize = 2,
            });
        await apiFactory.Services.SeedSyncEntryUpdateAsync(
            organization.Id,
            vault.Id,
            entryIds[0],
            user.Id,
            baseRevision: 1,
            seed: 96);
        await apiFactory.Services.SeedSyncEntryUpdateAsync(
            organization.Id,
            vault.Id,
            entryIds[0],
            user.Id,
            baseRevision: 2,
            memberIndexRevision: 2,
            agentDiscoveryRevision: 2,
            newKeyVersion: 2,
            seed: 128);
        var deltaUpdatedAt = (await LoadEntryUpdatedAtAsync(
            organization.Id,
            vault.Id,
            [entryIds[0]]))[entryIds[0]];
        var deltaRequest = new GetMemberDeltaRequest
        {
            VaultId = vault.Id,
            AfterSequence = firstSnapshot.SnapshotBaseSequence,
            PageSize = 2,
        };
        var (deltaResponse, delta) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            MemberDeltaResponse>(deltaRequest);
        var (retryResponse, retry) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            MemberDeltaResponse>(deltaRequest);

        // Then
        firstSnapshot.SnapshotBaseSequence.ShouldBe("3");
        firstSnapshot.NextCursor.ShouldNotBeNull();
        finalSnapshot!.NextCursor.ShouldBeNull();
        firstSnapshot.Items.Concat(finalSnapshot.Items).Select(x => x.EntryId).ShouldBe(entryIds, ignoreOrder: true);
        foreach (var item in firstSnapshot.Items.Concat(finalSnapshot.Items))
        {
            item.UpdatedAt.ShouldBe(snapshotUpdatedAt[item.EntryId]);
            var entryKey = item.EntryKey.ShouldNotBeNull();
            entryKey.EntryId.ShouldBe(item.EntryId);
            entryKey.KeyVersion.ShouldBe(item.CurrentKeyVersion!.Value);
        }
        deltaResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        delta!.DeltaUpperBound.ShouldBe("5");
        delta.AppliedThroughSequence.ShouldBe("5");
        delta.ContinuationCursor.ShouldBeNull();
        delta.Items.Count.ShouldBe(1);
        delta.Items[0].EntryId.ShouldBe(entryIds[0]);
        delta.Items[0].CurrentRevision.ShouldBe("3");
        delta.Items[0].UpdatedAt.ShouldBe(deltaUpdatedAt);
        delta.Items[0].MemberIndexRevision.ShouldBe("2");
        delta.Items[0].CurrentKeyVersion.ShouldBe(2u);
        var currentEntryKey = delta.Items[0].EntryKey.ShouldNotBeNull();
        currentEntryKey.EntryId.ShouldBe(entryIds[0]);
        currentEntryKey.KeyVersion.ShouldBe(2u);
        currentEntryKey.EncodedSuitePayload.ShouldBe(
            EntryEnvelopeFaker.CreateKey(
                organization.Id,
                vault.Id,
                entryIds[0],
                keyVersion: 2,
                seed: 128).EncodedSuitePayload);
        retry!.Items.ShouldBe(delta.Items);
        retry.AppliedThroughSequence.ShouldBe(delta.AppliedThroughSequence);
        deltaResponse.Headers.GetValues("X-Palladin-Vault-Protocol").Single().ShouldBe("2");
        deltaResponse.Headers.GetValues("X-Palladin-Sync-Policy").Single().ShouldBe("1");
        deltaResponse.Content.Headers.ContentEncoding.Single().ShouldBe("identity");
    }

    [Fact]
    public async Task When_MemberDeltaExceedsTheJournalScanPage_Then_ContinuationKeepsTheStableUpperBound()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        var entryIds = new List<Guid>
        {
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0),
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 32),
            await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 64),
        };
        foreach (var (entryId, seed) in entryIds.Select((entryId, index) => (entryId, 96 + (index * 32))))
        {
            await apiFactory.Services.SeedSyncEntryUpdateAsync(
                organization.Id,
                vault.Id,
                entryId,
                user.Id,
                baseRevision: 1,
                seed: seed);
        }

        // When
        var (_, firstPage) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            MemberDeltaResponse>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "3",
                PageSize = 2,
            });
        var (_, finalPage) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            MemberDeltaResponse>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                ContinuationCursor = firstPage!.ContinuationCursor,
                PageSize = 2,
            });

        // Then
        firstPage.DeltaUpperBound.ShouldBe("6");
        firstPage.AppliedThroughSequence.ShouldBe("5");
        firstPage.Items.Count.ShouldBe(2);
        firstPage.ContinuationCursor.ShouldNotBeNull();
        finalPage!.DeltaUpperBound.ShouldBe("6");
        finalPage.AppliedThroughSequence.ShouldBe("6");
        finalPage.Items.Count.ShouldBe(1);
        finalPage.ContinuationCursor.ShouldBeNull();
        firstPage.Items.Concat(finalPage.Items).Select(x => x.EntryId).ShouldBe(entryIds, ignoreOrder: true);
    }

    [Theory]
    [InlineData(EntryState.Archived)]
    [InlineData(EntryState.Deleted)]
    public async Task When_MemberEntryRemainsRecoverable_Then_DeltaReturnsItsEncryptedHead(EntryState state)
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            seed: 0);
        await apiFactory.Services.SeedSyncEntryUpdateAsync(
            organization.Id,
            vault.Id,
            entryId,
            user.Id,
            baseRevision: 1,
            seed: 32);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var entry = await writeContext.Entries.SingleAsync(
                x => x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.Id == entryId,
                TestContext.Current.CancellationToken);
            writeContext.Entry(entry).Property(x => x.State).CurrentValue = state;
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);

        // When
        var (response, delta) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            MemberDeltaResponse>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "1",
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        delta!.Items.Count.ShouldBe(1);
        delta.Items[0].EntryId.ShouldBe(entryId);
        delta.Items[0].Kind.ShouldBe("head");
        delta.Items[0].State.ShouldBe(state);
        delta.Items[0].MemberIndex.ShouldNotBeNull();
        var entryKey = delta.Items[0].EntryKey.ShouldNotBeNull();
        entryKey.EntryId.ShouldBe(entryId);
        entryKey.KeyVersion.ShouldBe(delta.Items[0].CurrentKeyVersion!.Value);
    }

    [Fact]
    public async Task When_ActiveAgentHasNoCurrentVaultProvisioning_Then_DiscoverySyncDoesNotRevealTheVault()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agentId = Guid.NewGuid();
        var fixture = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    id: agentId,
                    organizationId: organization.Id,
                    publicKey: fixture.X25519PublicKey,
                    signingPublicKey: fixture.RequestSigning.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            id: agentId,
            publicKey: fixture.X25519PublicKey,
            signingPublicKey: fixture.RequestSigning.PublicKeyBase64);
        var agentClient = apiFactory.CreateSignedAgentClient(
            agentId,
            apiKey,
            fixture.X25519PublicKey,
            fixture.RequestSigning);
        AddSyncHeaders(agentClient);

        // When
        var response = await agentClient.PostAsJsonAsync(
            $"api/agent/vaults/{vault.Id}/discovery/sync/snapshot",
            new GetAgentDiscoverySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("ciphertext", Case.Insensitive);
    }

    [Fact]
    public async Task When_SnapshotCursorIsReplayedAgainstAnotherVault_Then_RejectsIt()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var firstVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var secondVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, firstVault.Id, user.Id, seed: 0);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, firstVault.Id, user.Id, seed: 32);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, secondVault.Id, user.Id, seed: 64);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, secondVault.Id, user.Id, seed: 96);
        var (_, firstPage) = await client.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest,
            MemberSnapshotResponse>(new GetMemberSnapshotRequest
            {
                VaultId = firstVault.Id,
                PageSize = 1,
            });

        // When
        var response = await client.PostAsJsonAsync(
            $"api/vaults/{secondVault.Id}/sync/snapshot",
            new GetMemberSnapshotRequest
            {
                VaultId = secondVault.Id,
                Cursor = firstPage!.NextCursor,
                PageSize = 1,
            },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("invalid-cursor");
    }

    [Fact]
    public async Task When_KeyGenerationChangesBetweenSnapshotPages_Then_CursorIsRejected()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 32);
        var (_, firstPage) = await client.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest,
            MemberSnapshotResponse>(new GetMemberSnapshotRequest
            {
                VaultId = vault.Id,
                PageSize = 1,
            });

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().Vaults
                .Where(x => x.OrganizationId == organization.Id && x.Id == vault.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.MemberKeyGeneration, new MemberKeyGeneration(2)),
                    TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/sync/snapshot",
            new GetMemberSnapshotRequest
            {
                VaultId = vault.Id,
                Cursor = firstPage!.NextCursor,
                PageSize = 1,
            },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("invalid-cursor");
    }

    [Fact]
    public async Task When_MemberCursorIsBelowRetentionFloor_Then_ResetIsRequiredWithoutFalseCompletion()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddSyncHeaders(client);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var trackedVault = await writeContext.Vaults.SingleAsync(
                x => x.OrganizationId == organization.Id && x.Id == vault.Id,
                TestContext.Current.CancellationToken);
            trackedVault.AdvanceRetentionFloors(
                new MemberSequence(1),
                new DiscoverySequence(0),
                user.Id,
                apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // When
        var (response, reset) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            VaultSyncResetResponse>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "0",
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        reset!.Outcome.ShouldBe("resetRequired");
        reset.CurrentSequence.ShouldBe("1");
        reset.MinRetainedSequence.ShouldBe("1");
        reset.NewSnapshotRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task When_SyncCompatibilityHeadersAreMissing_Then_Returns426BeforeCiphertext()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var privilegedClient = apiFactory.CreateAuthenticatedClient(user);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);

        // When
        var response = await privilegedClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/sync/snapshot",
            new GetMemberSnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe((HttpStatusCode)426);
        body.ShouldContain("unsupported-protocol");
        body.ShouldNotContain("ciphertext", Case.Insensitive);
    }

    private static void AddSyncHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        client.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "1");
    }

    private async Task<Dictionary<Guid, Instant>> LoadEntryUpdatedAtAsync(
        Guid organizationId,
        Guid vaultId,
        IReadOnlyCollection<Guid> entryIds)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VaultDomainReadContext>().Entries
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == vaultId
                        && entryIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.UpdatedAt, TestContext.Current.CancellationToken);
    }

    private async Task DeactivateAgentAsync(AgentDeactivatedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<OnAgentDeactivated>(scope.ServiceProvider);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }

    private async Task RevokeEnvelopeAsync(Guid organizationId, Guid vaultId, Guid agentId, Instant revokedAt)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await writeContext.AgentVaultDiscoveryEnvelopes
            .Where(x => x.OrganizationId == organizationId && x.VaultId == vaultId && x.AgentId == agentId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.RevokedAt, revokedAt),
                TestContext.Current.CancellationToken);
    }
}
