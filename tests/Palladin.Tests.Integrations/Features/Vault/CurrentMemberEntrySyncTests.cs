using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class CurrentMemberEntrySyncTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_CurrentMemberTakesSnapshot_Then_EveryHeadAndAccessBindingIsComplete()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            seed: 0);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);

        var (response, snapshot) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest
            {
                VaultId = vault.Id,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Palladin-Vault-Protocol").Single().ShouldBe("2");
        response.Headers.GetValues("X-Palladin-Sync-Policy").Single().ShouldBe("2");
        response.Content.Headers.ContentEncoding.Single().ShouldBe("identity");
        snapshot.ShouldNotBeNull();
        snapshot.SnapshotBaseSequence.ShouldBe("1");
        snapshot.NextCursor.ShouldBeNull();
        var item = snapshot.Items.Single();
        item.EntryId.ShouldBe(entryId);
        item.Kind.ShouldBe("head");
        item.CurrentRevision.ShouldBe("1");
        item.MemberIndexRevision.ShouldBe(item.CurrentRevision);
        item.CurrentKeyVersion.ShouldBe(1u);
        item.EntryKey.ShouldNotBeNull();
        item.MemberIndex.ShouldNotBeNull();
        item.MemberSecret.ShouldNotBeNull();
        item.EntryKey.Descriptor.ResourceRevision.ShouldBe(item.CurrentRevision);
        item.MemberIndex.Descriptor.ResourceRevision.ShouldBe(item.CurrentRevision);
        item.MemberSecret.Descriptor.ResourceRevision.ShouldBe(item.CurrentRevision);
        item.EntryKey.Descriptor.Scope.OrganizationId.ShouldBe(organization.Id);
        item.EntryKey.Descriptor.Scope.VaultId.ShouldBe(vault.Id);
        item.EntryKey.Descriptor.Scope.EntryId.ShouldBe(entryId);

        var access = snapshot.AccessContext;
        access.ContextVersion.ShouldBe((ushort)1);
        access.PrincipalId.ShouldBe(user.Id);
        access.OrganizationId.ShouldBe(organization.Id);
        access.OrganizationMembershipGeneration.ShouldBe("1");
        access.VaultId.ShouldBe(vault.Id);
        access.MemberId.ShouldBe(user.Id);
        access.MemberKeyGeneration.ShouldBe(vault.MemberKeyGeneration.Value);
        access.VaultKeyVersion.ShouldBe(vault.CurrentVaultKeyVersion.Value);
        access.MemberRecipientKeyVersion.ShouldBe(snapshot.MemberVaultKey.RecipientMemberKeyVersion);
        access.MemberRecipientKeyFingerprint.ShouldBe(snapshot.MemberVaultKey.RecipientMemberKeyFingerprint);
        access.OfflinePolicy.ShouldBe("24h");
        access.OfflinePolicyVersion.ShouldBe(1u);
        access.NotAfter.ShouldBe(access.IssuedAt + Duration.FromHours(24));
        snapshot.MemberVaultKey.OrganizationId.ShouldBe(organization.Id);
        snapshot.MemberVaultKey.VaultId.ShouldBe(vault.Id);
        snapshot.MemberVaultKey.MemberId.ShouldBe(user.Id);
        snapshot.MemberVaultKey.VkVersion.ShouldBe(access.VaultKeyVersion);
        snapshot.MemberVaultKey.MemberKeyGeneration.ShouldBe(access.MemberKeyGeneration);
    }

    [Fact]
    public async Task When_SnapshotPageGrows_Then_EfCommandCountDoesNotGrowPerEntry()
    {
        using var counter = new EfCommandCounter();
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);

        counter.BeginWindow();
        var (smallResponse, smallPage) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest
            {
                VaultId = vault.Id,
                PageSize = 200,
            });
        var smallCommandCount = counter.EndWindow();

        foreach (var index in Enumerable.Range(1, 39))
        {
            await apiFactory.Services.SeedSyncEntryAsync(
                organization.Id,
                vault.Id,
                user.Id,
                seed: index * 32);
        }

        counter.BeginWindow();
        var (largeResponse, largePage) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest
            {
                VaultId = vault.Id,
                PageSize = 200,
            });
        var largeCommandCount = counter.EndWindow();

        smallResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        largeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        smallPage!.Items.Count.ShouldBe(1);
        largePage!.Items.Count.ShouldBe(40);
        smallCommandCount.ShouldBeGreaterThan(0);
        largeCommandCount.ShouldBe(smallCommandCount);
        largeCommandCount.ShouldBeLessThanOrEqualTo(8);
    }

    [Fact]
    public async Task When_CurrentHeadChangesDuringSnapshotWindow_Then_ClosingDeltaReturnsCompleteLatestHead()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(
            organization.Id,
            vault.Id,
            user.Id,
            seed: 0);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);
        var (_, snapshot) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id });
        await apiFactory.Services.SeedSyncEntryUpdateAsync(
            organization.Id,
            vault.Id,
            entryId,
            user.Id,
            baseRevision: 1,
            seed: 32);

        var request = new GetCurrentMemberEntryDeltaRequest
        {
            VaultId = vault.Id,
            AfterSequence = snapshot!.SnapshotBaseSequence,
        };
        var (response, delta) = await client.POSTAsync<
            GetCurrentMemberEntryDeltaEndpoint,
            GetCurrentMemberEntryDeltaRequest,
            CurrentMemberEntryDeltaResponse>(request);
        var (_, retry) = await client.POSTAsync<
            GetCurrentMemberEntryDeltaEndpoint,
            GetCurrentMemberEntryDeltaRequest,
            CurrentMemberEntryDeltaResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        delta.ShouldNotBeNull();
        delta.DeltaUpperBound.ShouldBe("2");
        delta.AppliedThroughSequence.ShouldBe("2");
        delta.ContinuationCursor.ShouldBeNull();
        var item = delta.Items.Single();
        item.EntryId.ShouldBe(entryId);
        item.Kind.ShouldBe("head");
        item.CurrentRevision.ShouldBe("2");
        item.MemberIndexRevision.ShouldBe("2");
        item.EntryKey.ShouldNotBeNull();
        item.MemberIndex.ShouldNotBeNull();
        item.MemberSecret.ShouldNotBeNull();
        item.MemberSecret.Descriptor.ResourceRevision.ShouldBe("2");
        retry!.DeltaUpperBound.ShouldBe(delta.DeltaUpperBound);
        retry.AppliedThroughSequence.ShouldBe(delta.AppliedThroughSequence);
        retry.AccessContext.ShouldBe(delta.AccessContext);
        retry.MemberVaultKey.ShouldBe(delta.MemberVaultKey);
        retry.Items.Single().ShouldBe(item);
        retry.ContinuationCursor.ShouldBeNull();
    }

    [Fact]
    public async Task When_CursorFallsBelowRetentionFloor_Then_DeltaRequiresResetWithoutCiphertext()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
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
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/delta",
            new GetCurrentMemberEntryDeltaRequest
            {
                VaultId = vault.Id,
                AfterSequence = "0",
            },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.ShouldContain("resetRequired");
        body.ShouldContain("newSnapshotRequired");
        body.ShouldNotContain("memberSecret", Case.Insensitive);
        body.ShouldNotContain("encodedSuitePayload", Case.Insensitive);
    }

    [Theory]
    [InlineData(OrganizationOfflineAccessPolicy.Disabled, "disabled", 0)]
    [InlineData(OrganizationOfflineAccessPolicy.OneHour, "1h", 1)]
    [InlineData(OrganizationOfflineAccessPolicy.FourHours, "4h", 4)]
    public async Task When_OrganizationPolicyChanges_Then_NewContextUsesOnlyThatFiniteLease(
        OrganizationOfflineAccessPolicy policy,
        string wireValue,
        int hours)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        await SetOfflinePolicyAsync(organization.Id, policy);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);

        var (response, snapshot) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        snapshot!.AccessContext.OfflinePolicy.ShouldBe(wireValue);
        snapshot.AccessContext.OfflinePolicyVersion.ShouldBe(2u);
        snapshot.AccessContext.NotAfter.ShouldBe(snapshot.AccessContext.IssuedAt + Duration.FromHours(hours));
    }

    [Fact]
    public async Task When_PolicyTwoIsMissingOrCallerTargetsForeignOrganization_Then_NoCiphertextIsReturned()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        await SeedMatchingMemberKeyDirectoryAsync(owner.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, owner.Id, seed: 0);
        var missingHeadersClient = apiFactory.CreateAuthenticatedClient(owner);

        var unsupported = await missingHeadersClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/snapshot",
            new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);
        var (attacker, attackerOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var attackerClient = apiFactory.CreateAuthenticatedClient(attacker);
        AddCurrentEntrySyncHeaders(attackerClient);
        var forbidden = await attackerClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/snapshot",
            new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);

        attackerOrganization.Id.ShouldNotBe(organization.Id);
        unsupported.StatusCode.ShouldBe((HttpStatusCode)426);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await unsupported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("memberSecret", Case.Insensitive);
        (await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("memberSecret", Case.Insensitive);
    }

    [Fact]
    public async Task When_IndependentRecipientAuthorityDoesNotMatchWrapper_Then_FailsClosedWithoutCiphertext()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeContext.MemberKeyDirectory
                .Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.Fingerprint, new byte[VaultProtocol.FingerprintBytes]),
                    TestContext.Current.CancellationToken);
        }
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/snapshot",
            new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldNotContain("memberSecret", Case.Insensitive);
        body.ShouldNotContain("memberVaultKey", Case.Insensitive);
    }

    [Fact]
    public async Task When_OfflinePolicyChangesBetweenSnapshotPages_Then_CursorRequiresReset()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 32);
        var client = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(client);
        var (_, firstPage) = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest,
            CurrentMemberEntrySnapshotResponse>(new GetCurrentMemberEntrySnapshotRequest
            {
                VaultId = vault.Id,
                PageSize = 1,
            });
        firstPage!.NextCursor.ShouldNotBeNull();
        await SetOfflinePolicyAsync(organization.Id, OrganizationOfflineAccessPolicy.OneHour);

        var request = new GetCurrentMemberEntrySnapshotRequest
        {
            VaultId = vault.Id,
            Cursor = firstPage.NextCursor,
            PageSize = 1,
        };
        var staleSessionResponse = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/snapshot",
            request,
            TestContext.Current.CancellationToken);
        var freshClient = apiFactory.CreateAuthenticatedClient(user);
        AddCurrentEntrySyncHeaders(freshClient);
        var response = await freshClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/current-entries/sync/snapshot",
            request,
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        staleSessionResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await staleSessionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("memberSecret", Case.Insensitive);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.ShouldContain("resetRequired");
        body.ShouldNotContain("memberSecret", Case.Insensitive);
    }

    [Fact]
    public async Task When_OrganizationMembershipIsRemoving_Then_CurrentEntrySyncFailsClosed()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        await SeedMatchingMemberKeyDirectoryAsync(member.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, member.Id, seed: 0);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var membership = await writeContext.OrganizationMembers.SingleAsync(x =>
                x.OrganizationId == organization.Id && x.UserId == member.Id);
            membership.RequestRemoval(Guid.NewGuid(), owner.Id, apiFactory.FakeClock.GetCurrentInstant());
            membership.FetchEvents();
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = apiFactory.CreateAuthenticatedClient(member);
        AddCurrentEntrySyncHeaders(client);
        var response = await client.POSTAsync<
            GetCurrentMemberEntrySnapshotEndpoint,
            GetCurrentMemberEntrySnapshotRequest>(new GetCurrentMemberEntrySnapshotRequest { VaultId = vault.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_LegacyPolicyOneRouteIsUsed_Then_ResponseShapeRemainsSecretFree()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedMatchingMemberKeyDirectoryAsync(user.Id);
        await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id, seed: 0);
        var client = apiFactory.CreateAuthenticatedClient(user);
        client.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        client.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "1");

        var response = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/sync/snapshot",
            new GetMemberSnapshotRequest { VaultId = vault.Id },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("memberIndex");
        body.ShouldNotContain("memberSecret", Case.Insensitive);
        body.ShouldNotContain("accessContext", Case.Insensitive);
        body.ShouldNotContain("memberVaultKey", Case.Insensitive);
    }

    private static void AddCurrentEntrySyncHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Palladin-Vault-Protocol", "2");
        client.DefaultRequestHeaders.Add("X-Palladin-Sync-Policy", "2");
    }

    private async Task SetOfflinePolicyAsync(
        Guid organizationId,
        OrganizationOfflineAccessPolicy policy)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var organization = await writeContext.Organizations.SingleAsync(
            x => x.Id == organizationId,
            TestContext.Current.CancellationToken);
        organization.SetOfflineAccessPolicy(policy).ShouldBeTrue();
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedMatchingMemberKeyDirectoryAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await writeContext.MemberKeyDirectory
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        writeContext.MemberKeyDirectory.Add(MemberKeyDirectoryEntry.Create(
            userId,
            new MemberRecipientKeyVersion(1),
            Enumerable.Range(0, VaultProtocol.FingerprintBytes).Select(x => (byte)x).ToArray(),
            new byte[VaultProtocol.FingerprintBytes],
            apiFactory.FakeClock.GetCurrentInstant()));
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class EfCommandCounter :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable allListenersSubscription;
        private IDisposable? efSubscription;
        private int enabled;
        private int count;

        internal EfCommandCounter()
        {
            allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        internal void BeginWindow()
        {
            Interlocked.Exchange(ref count, 0);
            Volatile.Write(ref enabled, 1);
        }

        internal int EndWindow()
        {
            Volatile.Write(ref enabled, 0);
            return Volatile.Read(ref count);
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                efSubscription = value.Subscribe(this);
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Volatile.Read(ref enabled) == 1
                && value.Key == RelationalEventId.CommandExecuted.Name)
            {
                Interlocked.Increment(ref count);
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void Dispose()
        {
            Volatile.Write(ref enabled, 0);
            efSubscription?.Dispose();
            allListenersSubscription.Dispose();
        }
    }

}
