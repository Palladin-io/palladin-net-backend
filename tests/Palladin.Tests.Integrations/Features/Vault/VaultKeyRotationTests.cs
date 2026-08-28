using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using MassTransit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class VaultKeyRotationTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_FullRotationIsStartedTwice_Then_ReturnsOneRequirement()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var request = new StartVaultKeyRotationRequest { VaultId = vault.Id };

        // When
        var (firstResponse, first) = await client.POSTAsync<
            StartVaultKeyRotationEndpoint,
            StartVaultKeyRotationRequest,
            VaultKeyRotationResponse>(request);
        var (retryResponse, retry) = await client.POSTAsync<
            StartVaultKeyRotationEndpoint,
            StartVaultKeyRotationRequest,
            VaultKeyRotationResponse>(request);

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry!.Id.ShouldBe(first!.Id);
        retry.TargetMemberKeyGeneration.ShouldBe(2U);
        retry.TargetKeyEpoch.ShouldBe(new VaultKeyEpochContract(2, 2, 2, 2));
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.CountAsync(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id))
            .ShouldBe(1);
    }

    [Fact]
    public async Task When_NonMemberStartsRotation_Then_ReturnsForbiddenWithoutCreatingState()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(outsider, Permission.VaultManage);

        // When
        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations",
            new StartVaultKeyRotationRequest { VaultId = vault.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .VaultKeyRotations.AnyAsync(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_MemberClaimsRotation_Then_NewFenceIsPersistedAndListed()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var (_, started) = await client.POSTAsync<
            StartVaultKeyRotationEndpoint,
            StartVaultKeyRotationRequest,
            VaultKeyRotationResponse>(new StartVaultKeyRotationRequest { VaultId = vault.Id });

        // When
        var (claimResponse, claimed) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = started!.Id,
            });
        var (listResponse, pending) = await client.GETAsync<
            ListPendingVaultKeyRotationsEndpoint,
            ListPendingVaultKeyRotationsResponse>();

        // Then
        claimResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        claimed!.OrganizationId.ShouldBe(organization.Id);
        claimed.FencingToken.ShouldNotBe(Guid.Empty);
        claimed.Rotation.Status.ShouldBe(nameof(VaultKeyRotationStatus.Preparing));
        pending!.Items.Single().Id.ShouldBe(started.Id);
        pending.Items.Single().LeaseOwnerId.ShouldBe(user.Id);
    }

    [Fact]
    public async Task When_CompletePendingGenerationCommits_Then_AllVaultHeadsSwitchAtomically()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var script = await apiFactory.Services.SeedEntryAsync(
            vault.Id,
            user.Id,
            EntryFaker.Create(organizationId: organization.Id, vaultId: vault.Id, createdBy: user.Id)
                .RuleFor(entry => entry.DeliveryPolicy, GrantDeliveryPolicy.ExecOnly));
        var reference = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var scriptGrantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var agentPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray());
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            AgentStatus.Deactivated,
            agentId,
            agentPublicKey);
        var scriptPackage = ScriptExecutionPackage.Create(
            organization.Id,
            vault.Id,
            scriptGrantId,
            agentId,
            1,
            script.Id,
            1,
            1,
            1,
            1,
            VaultKeyFingerprint.Compute(Convert.FromBase64String(agentPublicKey), VaultKeyKind.AgentX25519),
            1,
            VaultKeyFingerprint.Compute(
                VaultTrustAnchorFaker.ManifestSigningPublicKey,
                VaultKeyKind.VaultSigningEd25519),
            Enumerable.Repeat((byte)0xA5, 32).ToArray(),
            Enumerable.Repeat((byte)0x5A, 64).ToArray(),
            new byte[64]);
        var scriptGrant = ScriptExecutionGrant.CreateProactively(
            scriptGrantId,
            vault.Id,
            organization.Id,
            agentId,
            agentPublicKey,
            script.Id,
            [
                ScriptExecutionScope.Create(
                    organization.Id, vault.Id, scriptGrantId, script.Id, 1, true),
                ScriptExecutionScope.Create(
                    organization.Id, vault.Id, scriptGrantId, reference.Id, 1, false),
            ],
            scriptPackage,
            null,
            5,
            "uses",
            user.Id,
            new GrantNames("agent", "script", "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(),
            1);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(scriptGrant);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        var metadata = VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2));
        var memberKey = VaultFaker.CreateMemberKey(vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2));

        var (batchResponse, batch) = await client.PUTAsync<
            PrepareVaultKeyRotationBatchEndpoint,
            PrepareVaultKeyRotationBatchRequest,
            PrepareVaultKeyRotationBatchResponse>(new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(metadata),
                MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(memberKey)],
                DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                    organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                    organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
                VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
                EntryKeys =
                [
                    EntryEnvelopeFaker.CreateKey(
                        organization.Id, vault.Id, script.Id,
                        wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 81),
                    EntryEnvelopeFaker.CreateKey(
                        organization.Id, vault.Id, reference.Id,
                        wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 82),
                ],
            });

        batchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        batch!.AcceptedItems.ShouldBe(9);
        await using (var beforeScope = apiFactory.Services.CreateAsyncScope())
        {
            var before = await beforeScope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .Vaults.SingleAsync(x => x.OrganizationId == organization.Id && x.Id == vault.Id);
            before.MemberKeyGeneration.Value.ShouldBe(1U);
            before.CurrentVaultKeyVersion.Value.ShouldBe(1U);
        }

        var (commitResponse, committed) = await client.POSTAsync<
            CommitVaultKeyRotationEndpoint,
            CommitVaultKeyRotationRequest,
            VaultKeyRotationResponse>(new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });

        commitResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        committed!.Status.ShouldBe(nameof(VaultKeyRotationStatus.Committed));
        var retryResponse = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .Vaults.Include(x => x.VaultMemberKeyEnvelopes)
            .SingleAsync(x => x.OrganizationId == organization.Id && x.Id == vault.Id);
        persisted.MemberKeyGeneration.Value.ShouldBe(2U);
        persisted.MemberSequence.Value.ShouldBe(1UL);
        persisted.MinRetainedMemberSequence.Value.ShouldBe(1UL);
        persisted.CurrentKeyEpoch.ShouldBe(new VaultKeyEpoch(
            new VaultKeyVersion(2), new VdkVersion(2), new AgentMessageKeyVersion(2), new ManifestSigningKeyVersion(2)));
        persisted.AgentMessagePublicKey.ShouldBe(VaultTrustAnchorFaker.RotatedAgentMessagePublicKey);
        persisted.ManifestSigningPublicKey.ShouldBe(VaultTrustAnchorFaker.RotatedManifestSigningPublicKey);
        persisted.VaultMemberKeyEnvelopes.Single(x => x.MemberKeyGeneration.Value == 2).VaultKeyVersion.Value.ShouldBe(2U);
        var revokedScriptGrant = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .Grants.OfType<ScriptExecutionGrant>()
            .Include(grant => grant.ScriptExecutionPackage)
            .SingleAsync(grant => grant.Id == scriptGrantId);
        revokedScriptGrant.Status.ShouldBe(GrantStatus.Revoked);
        revokedScriptGrant.RevokedBySystem.ShouldBeTrue();
        revokedScriptGrant.ScriptExecutionPackage.ShouldBeNull();
        client.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        client.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "1");
        var (deltaResponse, reset) = await client.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest,
            VaultSyncResetResponse>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "0",
            });
        deltaResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        reset!.Outcome.ShouldBe("resetRequired");
        reset.CurrentSequence.ShouldBe("1");
        reset.MinRetainedSequence.ShouldBe("1");
    }

    [Fact]
    public async Task When_DiscoveryChangesAfterPreparation_Then_CommitReportsDirtyAndKeepsCurrentGeneration()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryFaker = EntryFaker.Create(organizationId: organization.Id, vaultId: vault.Id, createdBy: user.Id)
            .RuleFor(x => x.AgentDiscoveryRevision, new AgentDiscoveryRevision(1))
            .RuleFor(x => x.AgentDiscoveryRevisionHighWatermark, new AgentDiscoveryRevisionWatermark(1))
            .RuleFor(x => x.AgentDiscoveryProtocolVersion, VaultProtocol.CurrentVersion)
            .RuleFor(x => x.AgentDiscoveryCryptoSuiteId, CryptoSuiteId.XChaCha20Poly1305V1)
            .RuleFor(x => x.AgentDiscoveryVdkVersion, new VdkVersion(1))
            .RuleFor(x => x.AgentDiscoveryMemberKeyGeneration, new MemberKeyGeneration(1))
            .RuleFor(x => x.AgentDiscoveryEncodedSuitePayload,
                Enumerable.Range(0, VaultProtocol.NonceBytes + 48).Select(x => (byte)(x + 7)).ToArray());
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id, entryFaker);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var seededVault = await seedContext.Vaults.SingleAsync(x => x.OrganizationId == organization.Id && x.Id == vault.Id);
            seededVault.AllocateSequences(discoveryProjectionChanged: true, user.Id, apiFactory.FakeClock.GetCurrentInstant());
            await seedContext.SaveChangesAsync();
        }
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        var targetMetadata = VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2));
        var targetMemberKey = VaultFaker.CreateMemberKey(vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2));

        var batchResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(targetMetadata),
                MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(targetMemberKey)],
                DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                    organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                    organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
                VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
                EntryKeys = [EntryEnvelopeFaker.CreateKey(organization.Id, vault.Id, entry.Id,
                    wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 91)],
                EntryDiscoveries = [new RotationEntryDiscoveryContract("1",
                    EntryEnvelopeFaker.CreateAgentDiscovery(organization.Id, vault.Id, entry.Id,
                        revision: 1, vdkVersion: 2, memberKeyGeneration: 2, seed: 92))],
            });
        batchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updateResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/entries/{entry.Id}",
            EntryEnvelopeFaker.CreateUpdateRequest(organization.Id, vault.Id, entry.Id,
                baseRevision: 1, memberIndexRevision: 2, agentDiscoveryRevision: 2, seed: 101));
        var updateError = await updateResponse.Content.ReadAsStringAsync();
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK, updateError);

        var commitResponse = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });

        commitResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var incomplete = await commitResponse.Content.ReadFromJsonAsync<VaultKeyRotationIncompleteResponse>();
        incomplete!.DirtyEntryDiscoveryIds.ShouldContain(entry.Id);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .Vaults.SingleAsync(x => x.OrganizationId == organization.Id && x.Id == vault.Id);
        persisted.MemberKeyGeneration.Value.ShouldBe(1U);
        persisted.CurrentVdkVersion.Value.ShouldBe(1U);

        var reconciliationResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                EntryDiscoveries = [new RotationEntryDiscoveryContract("2",
                    EntryEnvelopeFaker.CreateAgentDiscovery(organization.Id, vault.Id, entry.Id,
                        revision: 2, vdkVersion: 2, memberKeyGeneration: 2, seed: 112))],
            });
        reconciliationResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reconciledCommit = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });
        reconciledCommit.StatusCode.ShouldBe(HttpStatusCode.OK);

        var staleWrite = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/entries/{entry.Id}",
            EntryEnvelopeFaker.CreateUpdateRequest(organization.Id, vault.Id, entry.Id,
                baseRevision: 2, memberKeyGeneration: 1, seed: 121));
        staleWrite.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_StaleFenceUploadsBatch_Then_RejectsWithoutReplacingPreparedMaterial()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);

        var response = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = Guid.NewGuid(),
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2))),
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .VaultKeyRotationPreparedItems.AnyAsync(x => x.RotationId == claim.Rotation.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_BatchExceedsBound_Then_RejectsBeforePersistingAnyMaterial()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        var memberKey = VaultEnvelopeContractMapper.ToContract(VaultFaker.CreateMemberKey(
            vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2)));

        var response = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                MemberVaultKeys = Enumerable.Repeat(memberKey, VaultProtocol.MaximumRotationBatchItems + 1).ToArray(),
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .VaultKeyRotationPreparedItems.AnyAsync(x => x.RotationId == claim.Rotation.Id)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("fingerprint")]
    public async Task When_PublicTrustAnchorDoesNotMatchTargetContract_Then_BatchFailsClosed(string mismatch)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        var valid = VaultContractFaker.CreateAgentMessagePublicKey();
        var invalid = mismatch switch
        {
            "kind" => valid with { KeyKind = VaultPublicKeyKindContract.ManifestSigningEd25519 },
            "version" => valid with { KeyVersion = 3 },
            "fingerprint" => valid with { Fingerprint = WebEncoders.Base64UrlEncode(new byte[32]) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        var response = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                VaultAgentMessagePublicKey = invalid,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .VaultKeyRotationPreparedItems.AnyAsync(x => x.RotationId == claim.Rotation.Id))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_PreparedEntryIsPurgedBeforeCommit_Then_StaleItemIsPrunedAndRotationCommits()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        var batch = new PrepareVaultKeyRotationBatchRequest
        {
            VaultId = vault.Id,
            RotationId = claim.Rotation.Id,
            FencingToken = claim.FencingToken,
            MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2))),
            MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(
                VaultFaker.CreateMemberKey(vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2)))],
            DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
            VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                organization.Id, vault.Id, 2, 2, 2, 2, 2, 2),
            VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
            VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
            EntryKeys = [EntryEnvelopeFaker.CreateKey(
                organization.Id, vault.Id, entry.Id,
                wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 81)],
        };
        (await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch", batch)).EnsureSuccessStatusCode();
        await using (var purgeScope = apiFactory.Services.CreateAsyncScope())
        {
            await purgeScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>()
                .Entries.Where(x => x.OrganizationId == organization.Id
                                    && x.VaultId == vault.Id
                                    && x.Id == entry.Id)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var (commitResponse, committed) = await client.POSTAsync<
            CommitVaultKeyRotationEndpoint,
            CommitVaultKeyRotationRequest,
            VaultKeyRotationResponse>(new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });

        commitResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        committed!.Status.ShouldBe(nameof(VaultKeyRotationStatus.Committed));
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var items = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .VaultKeyRotationPreparedItems.Where(x => x.RotationId == claim.Rotation.Id).ToListAsync();
        items.ShouldNotContain(x => x.Kind == VaultKeyRotationPreparedItemKind.EntryKey);
    }

    [Fact]
    public async Task When_MemberDirectoryRowIsLockedForCommit_Then_ConcurrentKeyAdvanceWaits()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        await SeedMemberDirectoryAsync(user.Id);
        await using var lockScope = apiFactory.Services.CreateAsyncScope();
        var domainContext = lockScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await domainContext.BeginTransactionAsync(TestContext.Current.CancellationToken);
        (await domainContext.LockMemberKeyDirectory([user.Id])
            .SingleAsync(TestContext.Current.CancellationToken)).UserId.ShouldBe(user.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await using var updateScope = apiFactory.Services.CreateAsyncScope();
        var consumer = new UpsertMemberKeyDirectoryConsumer(
            updateScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
        var context = Substitute.For<ConsumeContext<UpsertMemberKeyDirectoryCommand>>();
        context.Message.Returns(new UpsertMemberKeyDirectoryCommand(
            user.Id,
            2,
            Enumerable.Repeat((byte)0x44, 32).ToArray(),
            apiFactory.FakeClock.GetCurrentInstant().PlusTicks(1)));
        context.CancellationToken.Returns(timeout.Token);
        await Should.ThrowAsync<OperationCanceledException>(() => consumer.Consume(context));

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_RotationContainsMoreThanOneCommitPage_Then_AllEntryWrappersAreRewrapped()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entries = await SeedEntriesAsync(vault.Id, user.Id, CommitVaultKeyRotationEndpoint.CommitPageSize * 2 + 1);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        await PrepareRotationAsync(client, organization.Id, vault, user.Id, entries, claim);
        var retry = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                EntryKeys = [EntryEnvelopeFaker.CreateKey(
                    organization.Id, vault.Id, entries[0].Id,
                    wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 200)],
            }, TestContext.Current.CancellationToken);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        (await retry.Content.ReadFromJsonAsync<PrepareVaultKeyRotationBatchResponse>(
            TestContext.Current.CancellationToken))!.AcceptedItems.ShouldBe(0);

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persistedKeys = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .EntryKeys.Where(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedKeys.Count.ShouldBe(entries.Count);
        persistedKeys.ShouldAllBe(x => x.WrapperRevision.Value == 2
                                       && x.MemberKeyGeneration.Value == 2
                                       && x.WrappingKeyVersion.Value == 2);
    }

    [Fact]
    public async Task When_LaterCommitPageFails_Then_EarlierFlushedPagesAreRolledBack()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entries = await SeedEntriesAsync(vault.Id, user.Id, CommitVaultKeyRotationEndpoint.CommitPageSize + 1);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        await PrepareRotationAsync(client, organization.Id, vault, user.Id, entries, claim);
        var laterPageEntryId = entries.OrderBy(x => x.Id).ElementAt(CommitVaultKeyRotationEndpoint.CommitPageSize).Id;
        await using (var corruptScope = apiFactory.Services.CreateAsyncScope())
        {
            await corruptScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>()
                .VaultKeyRotationPreparedItems
                .Where(x => x.RotationId == claim.Rotation.Id
                            && x.Kind == VaultKeyRotationPreparedItemKind.EntryKey
                            && x.SubjectId == laterPageEntryId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.Payload, new byte[] { 0xff }),
                    TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persistedVault = await readContext.Vaults.SingleAsync(
            x => x.OrganizationId == organization.Id && x.Id == vault.Id,
            TestContext.Current.CancellationToken);
        persistedVault.MemberKeyGeneration.Value.ShouldBe(1U);
        (await readContext.EntryKeys
                .Where(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id)
                .Select(x => x.WrapperRevision.Value)
                .ToListAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(x => x == 1);
    }

    [Fact]
    public async Task When_LeaseExpiresWhileCommitWaitsForVaultFence_Then_CommitIsRejected()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
            var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
            await SeedMemberDirectoryAsync(user.Id);
            var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
            var claim = await StartAndClaimAsync(client, vault.Id);
            await PrepareRotationAsync(client, organization.Id, vault, user.Id, [], claim);
            await using var lockScope = apiFactory.Services.CreateAsyncScope();
            var lockContext = lockScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            await using var lockTransaction = await lockContext.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await lockContext.LockVault(organization.Id, vault.Id).SingleAsync(TestContext.Current.CancellationToken);

            var commitTask = client.PostAsJsonAsync(
                $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
                new CommitVaultKeyRotationRequest
                {
                    VaultId = vault.Id,
                    RotationId = claim.Rotation.Id,
                    FencingToken = claim.FencingToken,
                }, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            apiFactory.FakeClock.Advance(Duration.FromMinutes(3));
            await lockTransaction.CommitAsync(TestContext.Current.CancellationToken);

            (await commitTask).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_MoreThanOneAgentCoveragePageIsMissing_Then_AllAgentsAreReported()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agents = new List<Agent>();
        for (var index = 0; index < CommitVaultKeyRotationEndpoint.CommitPageSize + 1; index++)
        {
            agents.Add(await apiFactory.Services.SeedVaultAgentAsync(organization.Id));
        }
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);
        await PrepareRotationAsync(client, organization.Id, vault, user.Id, [], claim);

        // When
        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/commit",
            new CommitVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
            }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var incomplete = await response.Content.ReadFromJsonAsync<VaultKeyRotationIncompleteResponse>(
            TestContext.Current.CancellationToken);
        incomplete!.MissingAgentIds.ShouldBe(agents.Select(x => x.Id), ignoreOrder: true);
        incomplete.HasMoreIssues.ShouldBeFalse();
    }

    [Fact]
    public async Task When_CompleteEncryptedSeedExists_Then_ReclaimReturnsItWithoutPersistingRawKeys()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var firstClaim = await StartAndClaimAsync(client, vault.Id);
        firstClaim.PreparedMaterialReset.ShouldBeTrue();
        firstClaim.CurrentDiscoveryKey.ShouldNotBeNull();
        firstClaim.CurrentVaultPrivateKeys.Count.ShouldBe(2);
        firstClaim.PendingDiscoveryKey.ShouldBeNull();
        firstClaim.PendingVaultPrivateKeys.ShouldBeEmpty();

        var targetMemberKey = VaultEnvelopeContractMapper.ToContract(
            VaultFaker.CreateMemberKey(vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2)));
        var targetDiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
            organization.Id, vault.Id, 2, 2, 2, 2, 2, 2);
        var targetPrivateKeys = VaultContractFaker.CreatePrivateKeys(
            organization.Id, vault.Id, 2, 2, 2, 2, 2, 2);
        var seedResponse = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{firstClaim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = firstClaim.Rotation.Id,
                FencingToken = firstClaim.FencingToken,
                MemberVaultKeys = [targetMemberKey],
                DiscoveryKey = targetDiscoveryKey,
                VaultPrivateKeys = targetPrivateKeys,
            });
        seedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await seedResponse.Content.ReadAsStringAsync());

        var (_, resumed) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = firstClaim.Rotation.Id,
            });

        resumed!.PreparedMaterialReset.ShouldBeFalse();
        resumed.PendingMemberVaultKey.ShouldBe(targetMemberKey);
        resumed.PendingDiscoveryKey.ShouldBe(targetDiscoveryKey);
        resumed.PendingVaultPrivateKeys.ShouldBe(targetPrivateKeys, ignoreOrder: true);
        var serialized = System.Text.Json.JsonSerializer.Serialize(resumed);
        serialized.ShouldNotContain("\"VaultKey\":");
        serialized.ShouldNotContain("\"PrivateKey\":");
    }

    [Fact]
    public async Task When_StaleClaimCleanupRunsAfterANewerLeasePreparedItems_Then_NewItemsRemain()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var firstClaim = await StartAndClaimAsync(client, vault.Id);
        var targetMemberKey = VaultEnvelopeContractMapper.ToContract(
            VaultFaker.CreateMemberKey(vault.Scope, user.Id, new MemberKeyGeneration(2), new VaultKeyVersion(2)));
        var firstBatch = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{firstClaim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = firstClaim.Rotation.Id,
                FencingToken = firstClaim.FencingToken,
                MemberVaultKeys = [targetMemberKey],
            },
            TestContext.Current.CancellationToken);
        firstBatch.EnsureSuccessStatusCode();
        var (_, secondClaim) = await client.POSTAsync<
            ClaimVaultKeyRotationEndpoint,
            ClaimVaultKeyRotationRequest,
            ClaimVaultKeyRotationResponse>(new ClaimVaultKeyRotationRequest
            {
                VaultId = vault.Id,
                RotationId = firstClaim.Rotation.Id,
            });
        secondClaim!.PreparedMaterialReset.ShouldBeTrue();
        var secondBatch = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{firstClaim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = firstClaim.Rotation.Id,
                FencingToken = secondClaim.FencingToken,
                MemberVaultKeys = [targetMemberKey],
            },
            TestContext.Current.CancellationToken);
        secondBatch.EnsureSuccessStatusCode();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var staleLeaseCouldReset = await writeContext.ResetVaultKeyRotationPreparedItemsIfLeaseCurrentAsync(
            organization.Id,
            vault.Id,
            firstClaim.Rotation.Id,
            firstClaim.FencingToken,
            firstClaim.Rotation.LeaseRevision,
            TestContext.Current.CancellationToken);

        staleLeaseCouldReset.ShouldBeFalse();
        (await writeContext.VaultKeyRotationPreparedItems.CountAsync(
            x => x.OrganizationId == organization.Id
                 && x.VaultId == vault.Id
                 && x.RotationId == firstClaim.Rotation.Id,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_RotationSourceIsPaged_Then_EntryKeysAreBoundedAndStaleFenceFailsClosed()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedEntriesAsync(vault.Id, user.Id, 3);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);

        var first = await client.GetFromJsonAsync<VaultKeyRotationEntryKeySourceResponse>(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/source/entry-keys?fencingToken={claim.FencingToken}&pageSize=2",
            PalladinJsonSerializationSettings.DefaultOptions);
        first!.Items.Count.ShouldBe(2);
        first.NextAfterId.ShouldNotBeNull();
        first.NextAfterVersion.ShouldNotBeNull();
        var second = await client.GetFromJsonAsync<VaultKeyRotationEntryKeySourceResponse>(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/source/entry-keys?fencingToken={claim.FencingToken}&pageSize=2&afterId={first.NextAfterId}&afterVersion={first.NextAfterVersion}",
            PalladinJsonSerializationSettings.DefaultOptions);
        second!.Items.Count.ShouldBe(1);
        first.Items.Concat(second.Items).Select(x => (x.EntryId, x.KeyVersion)).Distinct().Count().ShouldBe(3);

        var stale = await client.GetAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/source/entry-keys?fencingToken={Guid.NewGuid()}&pageSize=2");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_MemberRotationSourceIsRead_Then_ItReturnsTheAuthenticatedRawRecipientKey()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMemberDirectoryAsync(user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);
        var claim = await StartAndClaimAsync(client, vault.Id);

        var source = await client.GetFromJsonAsync<VaultKeyRotationMemberSourceResponse>(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/source/members?fencingToken={claim.FencingToken}&pageSize=1",
            PalladinJsonSerializationSettings.DefaultOptions);

        var recipient = source!.Items.Single();
        recipient.MemberId.ShouldBe(user.Id);
        recipient.RecipientKeyVersion.ShouldBe(1u);
        WebEncoders.Base64UrlDecode(recipient.X25519PublicKey).ShouldBe(new byte[VaultProtocol.FingerprintBytes]);
        source.NextAfterId.ShouldBe(user.Id);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("entry-keys")]
    [InlineData("discoveries")]
    public async Task When_RotationSourceDoesNotExist_Then_ReturnsNotFound(string source)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);

        var response = await client.GetAsync(
            $"api/vaults/{vault.Id}/key-rotations/{Guid.NewGuid()}/source/{source}?fencingToken={Guid.NewGuid()}&pageSize=1");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<List<VaultEntry>> SeedEntriesAsync(Guid vaultId, Guid userId, int count)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults.SingleAsync(
            x => x.Id == vaultId, TestContext.Current.CancellationToken);
        var entries = Enumerable.Range(1, count)
            .Select(sequence => EntryFaker.Create(
                    organizationId: vault.OrganizationId, vaultId: vaultId, createdBy: userId)
                .RuleFor(x => x.Versions, (_, entry) => EntryFaker.CreateVersions(entry, (ulong)sequence))
                .Generate())
            .ToList();
        writeContext.Entries.AddRange(entries);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entries;
    }

    private static async Task PrepareRotationAsync(
        HttpClient client,
        Guid organizationId,
        Palladin.Module.Vault.Domain.Vault vault,
        Guid userId,
        IReadOnlyCollection<VaultEntry> entries,
        ClaimVaultKeyRotationResponse claim)
    {
        var response = await client.PutAsJsonAsync(
            $"api/vaults/{vault.Id}/key-rotations/{claim.Rotation.Id}/batch",
            new PrepareVaultKeyRotationBatchRequest
            {
                VaultId = vault.Id,
                RotationId = claim.Rotation.Id,
                FencingToken = claim.FencingToken,
                MemberVaultMetadata = VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMetadata(vault.Scope, 2, new MemberKeyGeneration(2), new VaultKeyVersion(2))),
                MemberVaultKeys = [VaultEnvelopeContractMapper.ToContract(
                    VaultFaker.CreateMemberKey(vault.Scope, userId, new MemberKeyGeneration(2), new VaultKeyVersion(2)))],
                DiscoveryKey = VaultContractFaker.CreateDiscoveryKey(
                    organizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultPrivateKeys = VaultContractFaker.CreatePrivateKeys(
                    organizationId, vault.Id, 2, 2, 2, 2, 2, 2),
                VaultAgentMessagePublicKey = VaultContractFaker.CreateAgentMessagePublicKey(),
                VaultManifestSigningPublicKey = VaultContractFaker.CreateManifestSigningPublicKey(),
                EntryKeys = entries.Select((entry, index) => EntryEnvelopeFaker.CreateKey(
                    organizationId, vault.Id, entry.Id,
                    wrapperRevision: 2, memberKeyGeneration: 2, wrappingKeyVersion: 2, seed: 200 + index)).ToArray(),
            }, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<ClaimVaultKeyRotationResponse> StartAndClaimAsync(HttpClient client, Guid vaultId)
    {
        var (_, started) = await client.POSTAsync<StartVaultKeyRotationEndpoint, StartVaultKeyRotationRequest, VaultKeyRotationResponse>(
            new StartVaultKeyRotationRequest { VaultId = vaultId });
        var (_, claim) = await client.POSTAsync<ClaimVaultKeyRotationEndpoint, ClaimVaultKeyRotationRequest, ClaimVaultKeyRotationResponse>(
            new ClaimVaultKeyRotationRequest { VaultId = vaultId, RotationId = started!.Id });
        return claim!;
    }

    private async Task SeedMemberDirectoryAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await writeContext.MemberKeyDirectory.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        writeContext.MemberKeyDirectory.Add(MemberKeyDirectoryEntry.Create(
            userId,
            new MemberRecipientKeyVersion(1),
            Enumerable.Range(0, VaultProtocol.FingerprintBytes).Select(x => (byte)x).ToArray(),
            new byte[VaultProtocol.FingerprintBytes],
            apiFactory.FakeClock.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
    }
}
