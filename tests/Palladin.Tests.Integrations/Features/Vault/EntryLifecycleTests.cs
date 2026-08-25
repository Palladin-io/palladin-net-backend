using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Core.Types;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Search.Features;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntryLifecycleTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_OrganizationMembershipIsRemoving_Then_AllEntryMutationsFailClosed()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var activeClient = apiFactory.CreateAuthenticatedClient(member);
        var entryId = await CreateEntryAsync(activeClient, organization.Id, vault.Id);

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var membership = await writeContext.OrganizationMembers.SingleAsync(x =>
                x.OrganizationId == organization.Id && x.UserId == member.Id);
            membership.RequestRemoval(
                Guid.NewGuid(), owner.Id, apiFactory.FakeClock.GetCurrentInstant());
            membership.FetchEvents();
            await writeContext.SaveChangesAsync();
        }

        var removingClient = apiFactory.CreateAuthenticatedClient(member);
        removingClient.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        removingClient.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "1");
        var snapshot = await removingClient.POSTAsync<
            GetMemberSnapshotEndpoint,
            GetMemberSnapshotRequest>(new GetMemberSnapshotRequest { VaultId = vault.Id });
        var delta = await removingClient.POSTAsync<
            GetMemberDeltaEndpoint,
            GetMemberDeltaRequest>(new GetMemberDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "0",
            });
        var search = await removingClient.POSTAsync<GlobalSearchEndpoint, GlobalSearchRequest>(
            new GlobalSearchRequest { Q = "member" });
        var archive = await removingClient.POSTAsync<
            ArchiveEntryEndpoint,
            ChangeEntryStateRequest>(EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id, vault.Id, entryId, 1, EntryOperation.Archived));
        var delete = await removingClient.POSTAsync<
            DeleteEntryEndpoint,
            ChangeEntryStateRequest>(EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id, vault.Id, entryId, 1, EntryOperation.Deleted));
        var restore = await removingClient.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest>(EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id, vault.Id, entryId, 1, EntryOperation.Restored));
        var destroy = await removingClient.POSTAsync<DestroyEntryEndpoint, DestroyEntryRequest>(
            new DestroyEntryRequest { VaultId = vault.Id, EntryId = entryId });

        new[] { snapshot, delta, search }
            .ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        new[] { archive, delete, restore, destroy }
            .ShouldAllBe(response => response.StatusCode == HttpStatusCode.Forbidden);
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<VaultDbReadContext>()
            .Entries.SingleAsync(x => x.Id == entryId);
        persisted.State.ShouldBe(EntryState.Active);
    }

    [Fact]
    public async Task When_EntryIsArchivedAndRestored_Then_EachTransitionAppendsOneCanonicalVersion()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var archive = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            1,
            EntryOperation.Archived);

        var (archiveResponse, archived) = await client.POSTAsync<
            ArchiveEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(archive);
        var (_, activeItems) = await client.GETAsync<
            ListEntriesEndpoint,
            ListEntriesRequest,
            ListEntriesResponse>(new ListEntriesRequest { VaultId = vault.Id });
        var (_, archivedItems) = await client.GETAsync<
            ListArchivedEntriesEndpoint,
            ListEntryLifecycleRequest,
            ListEntryLifecycleResponse>(new ListEntryLifecycleRequest { VaultId = vault.Id });
        var (_, archivedDetail) = await client.GETAsync<
            GetEntryEndpoint,
            GetEntryRequest,
            GetEntryResponse>(new GetEntryRequest { VaultId = vault.Id, EntryId = entryId });

        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        archived!.State.ShouldBe(EntryState.Archived);
        archived.CurrentRevision.ShouldBe("2");
        activeItems!.Items.ShouldBeEmpty();
        archivedItems!.Items.Single().Id.ShouldBe(entryId);
        archivedDetail!.AgentDiscoveryRevision.ShouldBeNull();
        archivedDetail.AgentDiscoveryRevisionHighWatermark.ShouldBe("1");

        var restore = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            2,
            EntryOperation.Restored,
            agentDiscoveryRevision: 2,
            seed: 64);
        var (restoreResponse, restored) = await client.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(restore);
        var (_, history) = await client.GETAsync<
            GetEntryHistoryEndpoint,
            GetEntryHistoryRequest,
            GetEntryHistoryResponse>(new GetEntryHistoryRequest
            {
                VaultId = vault.Id,
                EntryId = entryId,
            });

        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        restored!.State.ShouldBe(EntryState.Active);
        restored.CurrentRevision.ShouldBe("3");
        history!.CurrentRevision.ShouldBe("3");
        history.Items.Select(x => x.Operation).ShouldBe([
            EntryOperation.Restored,
            EntryOperation.Archived,
            EntryOperation.Created,
        ]);
        history.Items[0].MemberSecret.ShouldBe(restore.MemberSecret);
        history.Policy.MaximumVersions.ShouldBe(100);
        history.Policy.MaximumAgeDays.ShouldBe(365);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Entries.SingleAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.Id == entryId);
        persisted.AgentDiscoveryRevision!.Value.Value.ShouldBe(2UL);
        persisted.AgentDiscoveryRevisionHighWatermark.Value.ShouldBe(2UL);
        (await readContext.EntryVersions.CountAsync(x => x.EntryId == entryId)).ShouldBe(3);
    }

    [Fact]
    public async Task When_HistorySpansEntryKeyVersions_Then_EachItemCarriesItsExactEncryptedKeyEnvelope()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id, includeDiscovery: false);
        var update = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1,
            memberIndexRevision: 2,
            keyVersion: 1,
            newKeyVersion: 2);
        var (updateResponse, _) = await client.PUTAsync<
            UpdateEntryEndpoint,
            UpdateEntryRequest,
            UpdateEntryResponse>(update);

        var (historyResponse, history) = await client.GETAsync<
            GetEntryHistoryEndpoint,
            GetEntryHistoryRequest,
            GetEntryHistoryResponse>(new GetEntryHistoryRequest
            {
                VaultId = vault.Id,
                EntryId = entryId,
            });

        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        historyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        history!.Items.Select(item => item.KeyVersion).ShouldBe([2u, 1u]);
        history.Items.Select(item => item.EntryKey.KeyVersion).ShouldBe([2u, 1u]);
        history.Items[0].EntryKey.ShouldBe(update.NewEntryKey);
        history.Items[1].EntryKey.KeyVersion.ShouldBe(1u);
        history.Items.ShouldAllBe(item => item.EntryKey.EntryId == entryId);
    }

    [Fact]
    public async Task When_EntryIsDeleted_Then_GrantMaterialIsDestroyedAndOldGrantNeverReactivates()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var grant = GrantFaker.CreateGranular(
            vaultId: vault.Id,
            organizationId: organization.Id,
            agentId: agent.Id,
            entryId: entryId,
            createdBy: user.Id).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var delete = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            1,
            EntryOperation.Deleted);
        var (deleteResponse, deleted) = await client.POSTAsync<
            DeleteEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(delete);
        var (_, deletedItems) = await client.GETAsync<
            ListRecentlyDeletedEntriesEndpoint,
            ListEntryLifecycleRequest,
            ListEntryLifecycleResponse>(new ListEntryLifecycleRequest { VaultId = vault.Id });

        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        deleted!.State.ShouldBe(EntryState.Deleted);
        deletedItems!.Items.Single().Id.ShouldBe(entryId);
        deletedItems.Items.Single().RetentionExpiresAt.ShouldBe(
            deletedItems.Items.Single().DeletedAt + Duration.FromDays(30));
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            (await readContext.GrantEntryEnvelopes.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
            (await readContext.Grants.SingleAsync(x => x.Id == grant.Id)).Status.ShouldBe(GrantStatus.Revoked);
        }

        var restore = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            2,
            EntryOperation.Restored,
            agentDiscoveryRevision: 2,
            seed: 64);
        var (restoreResponse, _) = await client.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(restore);

        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var restoredScope = apiFactory.Services.CreateAsyncScope();
        var restoredReadContext = restoredScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await restoredReadContext.Grants.SingleAsync(x => x.Id == grant.Id)).Status.ShouldBe(GrantStatus.Revoked);
        (await restoredReadContext.GrantEntryEnvelopes.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_DeletedEntryRetentionExpires_Then_RestoreAndRecentlyDeletedListingFailClosed()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
            var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
            var client = apiFactory.CreateAuthenticatedClient(user);
            var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
            await client.POSTAsync<DeleteEntryEndpoint, ChangeEntryStateRequest, ChangeEntryStateResponse>(
                EntryEnvelopeFaker.CreateStateChangeRequest(
                    organization.Id,
                    vault.Id,
                    entryId,
                    1,
                    EntryOperation.Deleted));
            apiFactory.FakeClock.Advance(Duration.FromDays(30));

            var (restoreResponse, _) = await client.POSTAsync<
                RestoreEntryEndpoint,
                ChangeEntryStateRequest,
                ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
                organization.Id,
                vault.Id,
                entryId,
                2,
                EntryOperation.Restored,
                agentDiscoveryRevision: 2,
                seed: 64));
            var (_, recentlyDeleted) = await client.GETAsync<
                ListRecentlyDeletedEntriesEndpoint,
                ListEntryLifecycleRequest,
                ListEntryLifecycleResponse>(new ListEntryLifecycleRequest { VaultId = vault.Id });

            restoreResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            recentlyDeleted!.Items.ShouldBeEmpty();
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var persisted = await readContext.Entries.SingleAsync(x => x.Id == entryId);
            persisted.State.ShouldBe(EntryState.Deleted);
            persisted.CurrentRevision.Value.ShouldBe(2UL);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_DeletedEntryIsDestroyed_Then_LedgerPrecedesIdempotentContentPurge()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var grant = GrantFaker.CreateGranular(
            vaultId: vault.Id,
            organizationId: organization.Id,
            agentId: agent.Id,
            entryId: entryId,
            createdBy: user.Id).Generate();
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, grant.Id, entryId, grant.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(grant);
        var scriptGrantId = Guid.NewGuid();
        var scriptGrant = ScriptExecutionGrant.CreateProactively(
            scriptGrantId,
            vault.Id,
            organization.Id,
            agent.Id,
            agent.PublicKey,
            entryId,
            [ScriptExecutionScope.Create(organization.Id, vault.Id, scriptGrantId, entryId, 1, true)],
            ScriptExecutionPackage.Create(
                organization.Id,
                vault.Id,
                scriptGrantId,
                agent.Id,
                1,
                entryId,
                1,
                1,
                1,
                1,
                new byte[32],
                1,
                new byte[32],
                new byte[32],
                new byte[32],
                new byte[64]),
            null,
            null,
            "lifetime",
            user.Id,
            new GrantNames("agent", "script", "vault", "actor"),
            apiFactory.FakeClock.GetCurrentInstant(),
            1);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(scriptGrant);
        await client.POSTAsync<DeleteEntryEndpoint, ChangeEntryStateRequest, ChangeEntryStateResponse>(
            EntryEnvelopeFaker.CreateStateChangeRequest(
                organization.Id,
                vault.Id,
                entryId,
                1,
                EntryOperation.Deleted));
        apiFactory.EntryPurgeLedger.AppendAsync(default, default, default)
            .ReturnsForAnyArgs(new EntryPurgeLedgerRecord(
                1,
                "opaque",
                apiFactory.FakeClock.GetCurrentInstant()));
        var request = new DestroyEntryRequest { VaultId = vault.Id, EntryId = entryId };

        var first = await client.POSTAsync<DestroyEntryEndpoint, DestroyEntryRequest>(request);
        var retry = await client.POSTAsync<DestroyEntryEndpoint, DestroyEntryRequest>(request);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await apiFactory.EntryPurgeLedger.Received(1).AppendAsync(
            Arg.Is<EntryScope>(scope => scope.OrganizationId == organization.Id
                                        && scope.VaultId == vault.Id
                                        && scope.EntryId == entryId),
            Arg.Any<NodaTime.Instant>(),
            Arg.Any<CancellationToken>());
        await apiFactory.EntryAssetPurger.Received(1).PurgeAsync(
            Arg.Is<EntryScope>(scope => scope.OrganizationId == organization.Id
                                        && scope.VaultId == vault.Id
                                        && scope.EntryId == entryId),
            Arg.Any<CancellationToken>());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.Id == entryId)).ShouldBeFalse();
        (await readContext.EntryVersions.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
        (await readContext.EntryKeys.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
        (await readContext.GrantEntryScopes.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
        (await readContext.ScriptExecutionScopes.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
        var persistedVault = await readContext.Vaults.SingleAsync(x => x.Id == vault.Id);
        persistedVault.MinRetainedMemberSequence.Value.ShouldBe(2UL);
        persistedVault.MinRetainedDiscoverySequence.Value.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_PurgeLedgerAppendFails_Then_NoEntryContentIsDeleted()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        await client.POSTAsync<DeleteEntryEndpoint, ChangeEntryStateRequest, ChangeEntryStateResponse>(
            EntryEnvelopeFaker.CreateStateChangeRequest(
                organization.Id,
                vault.Id,
                entryId,
                1,
                EntryOperation.Deleted));
        var ledger = Substitute.For<IEntryPurgeLedger>();
        ledger.AppendAsync(default, default, default)
            .ReturnsForAnyArgs<Task<EntryPurgeLedgerRecord>>(_ =>
                throw new InvalidOperationException("ledger unavailable"));
        var assetPurger = Substitute.For<IEntryAssetPurger>();

        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var service = new EntryPurgeService(
                mutationScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                ledger,
                assetPurger,
                apiFactory.FakeClock);
            await Should.ThrowAsync<InvalidOperationException>(() => service.PurgeAsync(
                new EntryScope(organization.Id, vault.Id, entryId),
                user.Id,
                null,
                appendLedger: true,
                requireDeleted: true,
                TestContext.Current.CancellationToken));
        }

        await assetPurger.DidNotReceive().PurgeAsync(
            Arg.Any<EntryScope>(),
            Arg.Any<CancellationToken>());

        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.Id == entryId)).ShouldBeFalse();
        var durablePurge = await readContext.Entries.IgnoreQueryFilters().SingleAsync(x => x.Id == entryId);
        durablePurge.IsPurging.ShouldBeTrue();
        durablePurge.PurgeLedgerRequired.ShouldBeTrue();
        (await readContext.EntryVersions.CountAsync(x => x.EntryId == entryId)).ShouldBe(2);
        (await readContext.EntryKeys.CountAsync(x => x.EntryId == entryId)).ShouldBe(1);

        var recoveryLedger = Substitute.For<IEntryPurgeLedger>();
        recoveryLedger.AppendAsync(default, default, default)
            .ReturnsForAnyArgs(new EntryPurgeLedgerRecord(
                1,
                "opaque",
                apiFactory.FakeClock.GetCurrentInstant()));
        await using (var retryScope = apiFactory.Services.CreateAsyncScope())
        {
            var service = new EntryPurgeService(
                retryScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                recoveryLedger,
                assetPurger,
                apiFactory.FakeClock);
            (await service.PurgeAsync(
                new EntryScope(organization.Id, vault.Id, entryId),
                user.Id,
                null,
                appendLedger: true,
                requireDeleted: true,
                TestContext.Current.CancellationToken)).ShouldBe(EntryPurgeResult.Purged);
        }

        await using var completedScope = apiFactory.Services.CreateAsyncScope();
        var completedReadContext = completedScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await completedReadContext.Entries.IgnoreQueryFilters().AnyAsync(x => x.Id == entryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_BackupRestoresPurgedEntry_Then_StartupLedgerReplayDestroysItAgain()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var retainedEntryIds = new[]
        {
            await CreateEntryAsync(client, organization.Id, vault.Id),
            await CreateEntryAsync(client, organization.Id, vault.Id),
        };
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var scope = new EntryScope(organization.Id, vault.Id, entryId);
        var keyProvider = Substitute.For<IEntryPurgeKeyProvider>();
        keyProvider.GetKeyAsync(1, Arg.Any<CancellationToken>()).Returns(new byte[32]);
        var tokenGenerator = new EntryPurgeTokenGenerator(keyProvider);
        var record = new EntryPurgeLedgerRecord(
            1,
            await tokenGenerator.GenerateAsync(scope, 1, TestContext.Current.CancellationToken),
            apiFactory.FakeClock.GetCurrentInstant());
        var ledger = Substitute.For<IEntryPurgeLedger>();
        ledger.ReadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { record });
        var assetPurger = Substitute.For<IEntryAssetPurger>();

        await using (var replayScope = apiFactory.Services.CreateAsyncScope())
        {
            var purgeService = new EntryPurgeService(
                replayScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                ledger,
                assetPurger,
                apiFactory.FakeClock);
            var hook = new ReplayEntryPurgeLedgerApplicationStartingHook(
                replayScope.ServiceProvider.GetRequiredService<VaultDomainReadContext>(),
                ledger,
                tokenGenerator,
                purgeService,
                Options.Create(new EntryPurgeLedgerOptions
                {
                    Enabled = true,
                    BucketName = "palladin-purge-ledger",
                    KeyParameterPrefix = "/palladin/prod/vault/purge-key",
                }),
                Substitute.For<IHostEnvironment>())
            {
                PageSize = 1,
            };

            await hook.OnApplicationStartingAsync(TestContext.Current.CancellationToken);
        }

        await ledger.DidNotReceive().AppendAsync(
            Arg.Any<EntryScope>(),
            Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
        await assetPurger.Received(1).PurgeAsync(scope, Arg.Any<CancellationToken>());
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.Id == entryId)).ShouldBeFalse();
        (await readContext.Entries.CountAsync(x => retainedEntryIds.Contains(x.Id))).ShouldBe(2);
        (await readContext.EntryVersions.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
        (await readContext.EntryKeys.AnyAsync(x => x.EntryId == entryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_HistoryIsExplicitlyPurged_Then_CurrentVersionRemainsAndSyncFloorAdvances()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id, includeDiscovery: false);
        await client.PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(
            EntryEnvelopeFaker.CreateUpdateRequest(organization.Id, vault.Id, entryId, 1));
        await client.PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(
            EntryEnvelopeFaker.CreateUpdateRequest(organization.Id, vault.Id, entryId, 2, seed: 64));

        var (response, result) = await client.POSTAsync<
            PurgeEntryHistoryEndpoint,
            PurgeEntryHistoryRequest,
            PurgeEntryHistoryResponse>(new PurgeEntryHistoryRequest
            {
                VaultId = vault.Id,
                EntryId = entryId,
                BeforeRevision = "3",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.RemovedVersions.ShouldBe(2);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var versions = await readContext.EntryVersions.Where(x => x.EntryId == entryId).ToListAsync();
        versions.Single().Revision.Value.ShouldBe(3UL);
        var persistedVault = await readContext.Vaults.SingleAsync(x => x.Id == vault.Id);
        persistedVault.MinRetainedMemberSequence.Value.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_HistoryExceedsFrozenMaximum_Then_LifecycleJobRetainsLatestHundredVersions()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id, includeDiscovery: false);
        for (ulong baseRevision = 1; baseRevision <= 100; baseRevision++)
        {
            var (response, _) = await client.PUTAsync<
                UpdateEntryEndpoint,
                UpdateEntryRequest,
                UpdateEntryResponse>(EntryEnvelopeFaker.CreateUpdateRequest(
                organization.Id,
                vault.Id,
                entryId,
                baseRevision,
                seed: checked((int)baseRevision + 32)));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var jobScope = apiFactory.Services.CreateAsyncScope())
        {
            await jobScope.ServiceProvider
                .GetRequiredService<VaultEntryLifecycleJob>()
                .ExecuteAsync(TestContext.Current.CancellationToken);
        }

        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var revisions = await readContext.EntryVersions
            .Where(x => x.EntryId == entryId)
            .OrderBy(x => x.Revision)
            .Select(x => x.Revision.Value)
            .ToListAsync();
        revisions.Count.ShouldBe(100);
        revisions.First().ShouldBe(2UL);
        revisions.Last().ShouldBe(101UL);
        var persistedVault = await readContext.Vaults.SingleAsync(x => x.Id == vault.Id);
        persistedVault.MinRetainedMemberSequence.Value.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_OneNonCurrentVersionExceedsFrozenAge_Then_LifecycleJobPurgesIt()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
            var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
            var client = apiFactory.CreateAuthenticatedClient(user);
            var entryId = await CreateEntryAsync(client, organization.Id, vault.Id, includeDiscovery: false);
            apiFactory.FakeClock.Advance(Duration.FromDays(366));
            var (updateResponse, _) = await client.PUTAsync<
                UpdateEntryEndpoint,
                UpdateEntryRequest,
                UpdateEntryResponse>(EntryEnvelopeFaker.CreateUpdateRequest(
                organization.Id,
                vault.Id,
                entryId,
                1));
            updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            await using (var jobScope = apiFactory.Services.CreateAsyncScope())
            {
                await jobScope.ServiceProvider
                    .GetRequiredService<VaultEntryLifecycleJob>()
                    .ExecuteAsync(TestContext.Current.CancellationToken);
            }

            await using var assertionScope = apiFactory.Services.CreateAsyncScope();
            var readContext = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var revisions = await readContext.EntryVersions
                .Where(x => x.EntryId == entryId)
                .Select(x => x.Revision.Value)
                .ToListAsync(TestContext.Current.CancellationToken);
            revisions.ShouldBe([2UL]);
            var persistedVault = await readContext.Vaults.SingleAsync(
                x => x.Id == vault.Id,
                TestContext.Current.CancellationToken);
            persistedVault.MinRetainedMemberSequence.Value.ShouldBe(1UL);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    private async Task<Guid> CreateEntryAsync(
        HttpClient client,
        Guid organizationId,
        Guid vaultId,
        bool includeDiscovery = true)
    {
        var (_, challenge) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest { VaultId = vaultId });
        var entryId = challenge!.Items.Single().EntryId;
        var (response, _) = await client.POSTAsync<
            CreateEntryEndpoint,
            CreateEntryRequest,
            CreateEntryResponse>(EntryEnvelopeFaker.CreateRequest(
                organizationId,
                vaultId,
                entryId,
                includeDiscovery));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return entryId;
    }
}
