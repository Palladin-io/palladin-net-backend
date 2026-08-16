using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntryTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_MemberCreatesEntry_Then_StoresOneCanonicalHeadKeyAndVersion()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        var request = EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId);

        var (response, result) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        result.ShouldNotBeNull();
        result.Id.ShouldBe(entryId);
        result.CurrentRevision.ShouldBe("1");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .SingleAsync(x => x.OrganizationId == organization.Id
                              && x.VaultId == vault.Id
                              && x.Id == entryId);

        persisted.CurrentRevision.Value.ShouldBe(1UL);
        persisted.MemberIndexRevision.Value.ShouldBe(1UL);
        persisted.AgentDiscoveryRevision!.Value.Value.ShouldBe(1UL);
        persisted.CurrentKeyVersion.Value.ShouldBe(1U);
        persisted.Keys.Count.ShouldBe(1);
        persisted.Versions.Count.ShouldBe(1);
        VaultEnvelopeContractMapper.ToContract(persisted.GetMemberIndex()).ShouldBe(request.MemberIndex);
        VaultEnvelopeContractMapper.ToContract(persisted.GetAgentDiscovery()!).ShouldBe(request.AgentDiscovery);
        VaultEnvelopeContractMapper.ToContract(persisted.Versions.Single().GetMemberSecret())
            .ShouldBe(request.MemberSecret);
        persisted.GetType().GetProperty("Content").ShouldBeNull();
        persisted.GetType().GetProperty("MemberSecretCiphertext").ShouldBeNull();
    }

    [Fact]
    public async Task When_CreateIsRetriedExactly_Then_DoesNotAppendKeyOrVersion()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        var request = EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId);

        var (firstResponse, _) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(request);
        var (retryResponse, retryResult) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(request);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResult!.CurrentRevision.ShouldBe("1");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EntryKeys.CountAsync(x => x.OrganizationId == organization.Id
                                                     && x.VaultId == vault.Id
                                                     && x.EntryId == entryId)).ShouldBe(1);
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(1);
    }

    [Fact]
    public async Task When_CreatingEntry_Then_WaitsForStableVaultLifecycleLock()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        var request = EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await writeContext.LockVault(organization.Id, vault.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var createTask = client.POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(request);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        createTask.IsCompleted.ShouldBeFalse();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var (response, _) = await createTask;
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task When_ActiveFullGrantExists_Then_NewEntryFailsClosed()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        await apiFactory.Services.SeedFullGrantAsync(
            GrantFaker.CreateFull(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                createdBy: user.Id).Generate());

        var (response, _) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(
                EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.OrganizationId == organization.Id
                                                 && x.VaultId == vault.Id
                                                 && x.Id == entryId)).ShouldBeFalse();
        (await readContext.EntryCreationChallenges.SingleAsync(x => x.OrganizationId == organization.Id
                                                                     && x.VaultId == vault.Id
                                                                     && x.EntryId == entryId))
            .ConsumedAt.ShouldBeNull();
        var unchangedVault = await readContext.Vaults.SingleAsync(x => x.OrganizationId == organization.Id
                                                                   && x.Id == vault.Id);
        unchangedVault.MemberSequence.Value.ShouldBe(0UL);
        unchangedVault.DiscoverySequence.Value.ShouldBe(0UL);
    }

    [Fact]
    public async Task When_EncryptedImportIsRetried_Then_DoesNotDuplicateEntriesOrSequences()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var (challengeResponse, challengeResult) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = vault.Id,
                Count = 2,
            });
        challengeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var createRequests = challengeResult!.Items
            .Select((item, index) => EntryEnvelopeFaker.CreateRequest(
                organization.Id,
                vault.Id,
                item.EntryId,
                seed: index * 64))
            .ToList();
        var request = new ImportEntriesRequest
        {
            VaultId = vault.Id,
            Format = "canonical-test",
            Entries = createRequests.Select(item => new ImportEntryItem
            {
                EntryId = item.EntryId,
                EntryKey = item.EntryKey,
                MemberIndex = item.MemberIndex,
                MemberSecret = item.MemberSecret,
                AgentDiscovery = item.AgentDiscovery,
            }).ToList(),
        };

        var (firstResponse, firstResult) = await client
            .POSTAsync<ImportEntriesEndpoint, ImportEntriesRequest, ImportEntriesResponse>(request);
        var (retryResponse, retryResult) = await client
            .POSTAsync<ImportEntriesEndpoint, ImportEntriesRequest, ImportEntriesResponse>(request);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstResult!.ImportedCount.ShouldBe(2);
        retryResult!.ImportedCount.ShouldBe(2);
        retryResult.EntryIds.ShouldBe(firstResult.EntryIds);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.CountAsync(x => x.OrganizationId == organization.Id
                                                   && x.VaultId == vault.Id)).ShouldBe(2);
        var versions = await readContext.EntryVersions
            .Where(x => x.OrganizationId == organization.Id && x.VaultId == vault.Id)
            .ToListAsync();
        var sequences = versions.Select(x => x.MemberSequence.Value).OrderBy(x => x).ToList();
        sequences.ShouldBe([1UL, 2UL]);
    }

    [Fact]
    public async Task When_VaultChangesDuringImport_Then_StaleAttemptConflictsAndExactRetryUsesFreshSequence()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        var item = EntryEnvelopeFaker.CreateImportItem(organization.Id, vault.Id, entryId);
        var request = new ImportEntriesRequest
        {
            VaultId = vault.Id,
            Format = "canonical-test",
            Entries = [item],
        };
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var lockedVault = await writeContext.LockVault(organization.Id, vault.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var importTask = client.POSTAsync<ImportEntriesEndpoint, ImportEntriesRequest, ImportEntriesResponse>(request);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        importTask.IsCompleted.ShouldBeFalse();

        lockedVault.AllocateSequences(false, user.Id, apiFactory.FakeClock.GetCurrentInstant());
        await writeContext.CommitAsync(transaction, TestContext.Current.CancellationToken);
        var (staleResponse, _) = await importTask;
        staleResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var (retryResponse, retryResult) = await client
            .POSTAsync<ImportEntriesEndpoint, ImportEntriesRequest, ImportEntriesResponse>(request);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResult!.ImportedCount.ShouldBe(1);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var version = await readContext.EntryVersions.SingleAsync(x => x.EntryId == entryId);
        version.MemberSequence.Value.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_ActiveFullGrantExists_Then_EncryptedImportWrapsEveryEntryAtomically()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var (challengeResponse, challengeResult) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = vault.Id,
                Count = 2,
            });
        challengeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entryIds = challengeResult!.Items.Select(item => item.EntryId).ToList();
        var full = await apiFactory.Services.SeedFullGrantAsync(
            GrantFaker.CreateFull(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                createdBy: user.Id).Generate());
        var first = EntryEnvelopeFaker.CreateImportItem(organization.Id, vault.Id, entryIds[0]) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id, vault.Id, full.Id, entryIds[0], agent.PublicKey,
                    full.ExpiresAt, full.QueryLimit, agentId: agent.Id, methods: full.Methods),
            ],
        };
        var second = EntryEnvelopeFaker.CreateImportItem(organization.Id, vault.Id, entryIds[1], seed: 64) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id, vault.Id, full.Id, entryIds[1], agent.PublicKey,
                    full.ExpiresAt, full.QueryLimit, agentId: agent.Id, methods: full.Methods),
            ],
        };
        var request = new ImportEntriesRequest
        {
            VaultId = vault.Id,
            Format = "canonical-encrypted",
            Entries =
            [
                first,
                second,
            ],
        };

        var (response, _) = await client
            .POSTAsync<ImportEntriesEndpoint, ImportEntriesRequest, ImportEntriesResponse>(request);

        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, responseBody);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.CountAsync(x => x.OrganizationId == organization.Id
                                                   && x.VaultId == vault.Id)).ShouldBe(2);
        (await readContext.GrantEntryEnvelopes.CountAsync(x => x.GrantId == full.Id)).ShouldBe(2);
        var unchangedChallenges = await readContext.EntryCreationChallenges
            .Where(x => x.OrganizationId == organization.Id
                        && x.VaultId == vault.Id
                        && entryIds.Contains(x.EntryId))
            .ToListAsync();
        unchangedChallenges.Count.ShouldBe(2);
        unchangedChallenges.ShouldAllBe(x => x.ConsumedAt != null);
        var updatedVault = await readContext.Vaults.SingleAsync(x => x.OrganizationId == organization.Id
                                                                 && x.Id == vault.Id);
        updatedVault.MemberSequence.Value.ShouldBe(2UL);
        updatedVault.DiscoverySequence.Value.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_EncryptedImportOmitsAFullGrantEnvelope_Then_WholeBatchRollsBack()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var (challengeResponse, challengeResult) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = vault.Id,
                Count = 2,
            });
        challengeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entryIds = challengeResult!.Items.Select(item => item.EntryId).ToList();
        var full = await apiFactory.Services.SeedFullGrantAsync(
            GrantFaker.CreateFull(
                vaultId: vault.Id,
                organizationId: organization.Id,
                agentId: agent.Id,
                createdBy: user.Id).Generate());
        var first = EntryEnvelopeFaker.CreateImportItem(organization.Id, vault.Id, entryIds[0]) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id, vault.Id, full.Id, entryIds[0], agent.PublicKey,
                    full.ExpiresAt, full.QueryLimit, agentId: agent.Id),
            ],
        };
        var second = EntryEnvelopeFaker.CreateImportItem(organization.Id, vault.Id, entryIds[1], seed: 64);

        var (response, _) = await client.POSTAsync<
            ImportEntriesEndpoint,
            ImportEntriesRequest,
            ImportEntriesResponse>(new ImportEntriesRequest
            {
                VaultId = vault.Id,
                Format = "canonical-encrypted",
                Entries = [first, second],
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.OrganizationId == organization.Id
                                                 && x.VaultId == vault.Id)).ShouldBeFalse();
        (await readContext.GrantEntryEnvelopes.AnyAsync(x => x.GrantId == full.Id)).ShouldBeFalse();
        var challenges = await readContext.EntryCreationChallenges
            .Where(x => x.OrganizationId == organization.Id
                        && x.VaultId == vault.Id
                        && entryIds.Contains(x.EntryId))
            .ToListAsync();
        challenges.Count.ShouldBe(2);
        challenges.ShouldAllBe(x => x.ConsumedAt == null);
        var unchangedVault = await readContext.Vaults.SingleAsync(x => x.OrganizationId == organization.Id
                                                                   && x.Id == vault.Id);
        unchangedVault.MemberSequence.Value.ShouldBe(0UL);
        unchangedVault.DiscoverySequence.Value.ShouldBe(0UL);
    }

    [Fact]
    public async Task When_CreateUsesNoServerIssuedEntryId_Then_RejectsPayload()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = Guid.NewGuid();

        var (response, _) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(
                EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_CreateSubstitutesEnvelopeScope_Then_RejectsWholeTransition()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(client, vault.Id);
        var valid = EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId);
        var substituted = valid with
        {
            MemberSecret = valid.MemberSecret with
            {
                Descriptor = valid.MemberSecret.Descriptor with
                {
                    Scope = valid.MemberSecret.Descriptor.Scope with { OrganizationId = Guid.NewGuid() },
                },
            },
        };

        var (response, _) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(substituted);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Entries.AnyAsync(x => x.OrganizationId == organization.Id
                                                 && x.VaultId == vault.Id
                                                 && x.Id == entryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_PrivateOnlyUpdateIsRetried_Then_AppendsExactlyOneImmutableVersion()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1);

        var (firstResponse, firstResult) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);
        var (retryResponse, retryResult) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstResult!.CurrentRevision.ShouldBe("2");
        retryResult!.CurrentRevision.ShouldBe("2");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Entries.SingleAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.Id == entryId);
        persisted.CurrentRevision.Value.ShouldBe(2UL);
        persisted.MemberIndexRevision.Value.ShouldBe(1UL);
        persisted.AgentDiscoveryRevision!.Value.Value.ShouldBe(1UL);
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(2);
        (await readContext.EntryKeys.CountAsync(x => x.OrganizationId == organization.Id
                                                     && x.VaultId == vault.Id
                                                     && x.EntryId == entryId)).ShouldBe(1);
    }

    [Fact]
    public async Task When_UpdateRetryOmitsAnyCommittedTransitionPart_Then_ReturnsConflict()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var complete = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1,
            memberIndexRevision: 2,
            agentDiscoveryRevision: 2,
            newKeyVersion: 2,
            seed: 64);

        var (firstResponse, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(complete);
        var incompleteRetries = new[]
        {
            complete with { NewEntryKey = null },
            complete with { MemberIndex = null },
            complete with { AgentDiscoveryChanged = false, AgentDiscovery = null },
        };

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var incompleteRetry in incompleteRetries)
        {
            var (retryResponse, _) = await client
                .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(incompleteRetry);
            retryResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(2);
        (await readContext.EntryKeys.CountAsync(x => x.OrganizationId == organization.Id
                                                     && x.VaultId == vault.Id
                                                     && x.EntryId == entryId)).ShouldBe(2);
    }

    [Fact]
    public async Task When_ProjectionsChangeAfterPrivateUpdate_Then_TheirOwnRevisionsAdvance()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var privateUpdate = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1);
        await client.PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(privateUpdate);
        var projectionUpdate = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 2,
            memberIndexRevision: 2,
            agentDiscoveryRevision: 2,
            seed: 96);

        var (response, result) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(projectionUpdate);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.CurrentRevision.ShouldBe("3");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Entries.SingleAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.Id == entryId);
        persisted.CurrentRevision.Value.ShouldBe(3UL);
        persisted.MemberIndexRevision.Value.ShouldBe(2UL);
        persisted.AgentDiscoveryRevision!.Value.Value.ShouldBe(2UL);
    }

    [Fact]
    public async Task When_UpdateUsesStaleBaseRevision_Then_DoesNotAppendPartialVersion()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var first = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1,
            seed: 32);
        await client.PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(first);
        var stale = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1,
            seed: 160);

        var (response, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(stale);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(2);
    }

    [Fact]
    public async Task When_ActiveGrantCoversEntry_Then_UpdateFailsClosed()
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
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, grant.Id, entryId, grant.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(grant);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1);

        var (response, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(1);
    }

    [Fact]
    public async Task When_AllActiveGrantEnvelopesAdvance_Then_UpdateCommitsAtomically()
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
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, grant.Id, entryId, grant.Methods,
            grant.ExpiresAt, grant.QueryLimit, agentId: agent.Id, agentPublicKey: agent.PublicKey));
        await apiFactory.Services.SeedGranularGrantAsync(grant);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id,
                    vault.Id,
                    grant.Id,
                    entryId,
                    agent.PublicKey,
                    grant.ExpiresAt,
                    grant.QueryLimit,
                    entryRevision: 2,
                    envelopeRevision: 2,
                    grantKeyVersion: 2,
                    agentId: agent.Id,
                    methods: grant.Methods),
            ],
        };

        var (response, result) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.CurrentRevision.ShouldBe("2");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var envelope = await db.GrantEntryEnvelopes.SingleAsync(e => e.GrantId == grant.Id);
        envelope.EntryRevision.ShouldBe(2UL);
        envelope.GrantEnvelopeRevision.ShouldBe(2UL);
        envelope.GrantKeyVersion.ShouldBe(2U);
    }

    [Fact]
    public async Task When_StaleAndCurrentActiveScopesExist_Then_UpdateRefreshesAllCoverage()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var firstUpdate = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1);
        var (firstResponse, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(firstUpdate);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var staleFull = GrantFaker.CreateFull(
            vaultId: vault.Id,
            organizationId: organization.Id,
            agentId: agent.Id,
            createdBy: user.Id).Generate();
        staleFull.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, staleFull.Id, entryId, staleFull.Methods,
            staleFull.ExpiresAt, staleFull.QueryLimit, entryRevision: 1,
            agentId: agent.Id, agentPublicKey: agent.PublicKey));
        await apiFactory.Services.SeedFullGrantAsync(staleFull);
        var currentGranular = GrantFaker.CreateGranular(
            vaultId: vault.Id,
            organizationId: organization.Id,
            agentId: agent.Id,
            entryId: entryId,
            createdBy: user.Id).Generate();
        currentGranular.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, currentGranular.Id, entryId, currentGranular.Methods,
            currentGranular.ExpiresAt, currentGranular.QueryLimit, entryRevision: 2,
            agentId: agent.Id, agentPublicKey: agent.PublicKey));
        await apiFactory.Services.SeedGranularGrantAsync(currentGranular);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 2,
            seed: 64) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id,
                    vault.Id,
                    staleFull.Id,
                    entryId,
                    agent.PublicKey,
                    staleFull.ExpiresAt,
                    staleFull.QueryLimit,
                    entryRevision: 3,
                    envelopeRevision: 2,
                    grantKeyVersion: 2,
                    agentId: agent.Id,
                    methods: staleFull.Methods),
                GrantEnvelopeTestData.Contract(
                    organization.Id,
                    vault.Id,
                    currentGranular.Id,
                    entryId,
                    agent.PublicKey,
                    currentGranular.ExpiresAt,
                    currentGranular.QueryLimit,
                    entryRevision: 3,
                    envelopeRevision: 2,
                    grantKeyVersion: 2,
                    agentId: agent.Id,
                    methods: currentGranular.Methods),
            ],
        };

        var (response, result) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.CurrentRevision.ShouldBe("3");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.GrantEntryEnvelopes.SingleAsync(x => x.GrantId == staleFull.Id))
            .EntryRevision.ShouldBe(3UL);
        (await db.GrantEntryEnvelopes.SingleAsync(x => x.GrantId == currentGranular.Id))
            .EntryRevision.ShouldBe(3UL);
    }

    [Fact]
    public async Task When_ArchivedEntryIsRestored_Then_NextUpdateRefreshesRetainedActiveGrant()
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
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, grant.Id, entryId, grant.Methods,
            grant.ExpiresAt, grant.QueryLimit, agentId: agent.Id, agentPublicKey: agent.PublicKey));
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var (archiveResponse, _) = await client.POSTAsync<
            ArchiveEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id, vault.Id, entryId, 1, EntryOperation.Archived));
        var (restoreResponse, _) = await client.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id, vault.Id, entryId, 2, EntryOperation.Restored,
            agentDiscoveryRevision: 2, seed: 64));
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 3,
            seed: 96) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id,
                    vault.Id,
                    grant.Id,
                    entryId,
                    agent.PublicKey,
                    grant.ExpiresAt,
                    grant.QueryLimit,
                    entryRevision: 4,
                    envelopeRevision: 2,
                    grantKeyVersion: 2,
                    agentId: agent.Id,
                    methods: grant.Methods),
            ],
        };

        var (response, result) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.CurrentRevision.ShouldBe("4");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var envelope = await db.GrantEntryEnvelopes.SingleAsync(x => x.GrantId == grant.Id);
        envelope.EntryRevision.ShouldBe(4UL);
        envelope.GrantEnvelopeRevision.ShouldBe(2UL);
        envelope.GrantKeyVersion.ShouldBe(2U);
    }

    [Fact]
    public async Task When_GrantRefreshBroadensFieldScope_Then_UpdateRollsBackAtomically()
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
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            organization.Id, vault.Id, grant.Id, entryId, grant.Methods,
            grant.ExpiresAt, grant.QueryLimit));
        await apiFactory.Services.SeedGranularGrantAsync(grant);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    organization.Id,
                    vault.Id,
                    grant.Id,
                    entryId,
                    agent.PublicKey,
                    grant.ExpiresAt,
                    grant.QueryLimit,
                    entryRevision: 2,
                    envelopeRevision: 2,
                    grantKeyVersion: 2,
                    fieldIds: ["username", "password", "totp"],
                    agentId: agent.Id),
            ],
        };

        var (response, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                && x.VaultId == vault.Id
                                                && x.EntryId == entryId)).ShouldBe(1);
        var persistedScope = await db.GrantEntryScopes
            .Include(x => x.Envelope)
            .SingleAsync(x => x.GrantId == grant.Id && x.EntryId == entryId);
        persistedScope.FieldIds.ShouldBe("password\nusername");
        persistedScope.Envelope!.EntryRevision.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_NewEntryKeyOmitsMemberIndexReplacement_Then_RejectsWholeTransition()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var partial = EntryEnvelopeFaker.CreateUpdateRequest(
            organization.Id,
            vault.Id,
            entryId,
            baseRevision: 1,
            newKeyVersion: 2,
            seed: 64);

        var (response, _) = await client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(partial);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EntryVersions.CountAsync(x => x.OrganizationId == organization.Id
                                                         && x.VaultId == vault.Id
                                                         && x.EntryId == entryId)).ShouldBe(1);
        (await readContext.EntryKeys.CountAsync(x => x.OrganizationId == organization.Id
                                                     && x.VaultId == vault.Id
                                                     && x.EntryId == entryId)).ShouldBe(1);
    }

    [Fact]
    public async Task When_UserTargetsVaultOutsideTokenOrganization_Then_Returns403()
    {
        var (owner, ownerOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignVault = await apiFactory.Services.SeedVaultAsync(ownerOrganization.Id, owner.Id);
        var (attacker, attackerOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(attacker);

        var (response, _) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = foreignVault.Id,
            });

        attackerOrganization.Id.ShouldNotBe(ownerOrganization.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_EntryKeySubstitutesTenant_Then_CompositeForeignKeyRejectsIt()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var substitutedKey = VaultEnvelopeContractMapper.ToDomain(EntryEnvelopeFaker.CreateKey(
            Guid.NewGuid(),
            vault.Id,
            entryId,
            keyVersion: 2,
            seed: 96));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.EntryKeys.Add(substitutedKey);

        await Should.ThrowAsync<DbUpdateException>(() => writeContext.SaveChangesAsync());
    }

    [Fact]
    public async Task When_ExistingRevisionIsReplaced_Then_ImmutablePrimaryKeyRejectsIt()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var replacementSecret = VaultEnvelopeContractMapper.ToDomain(
            EntryEnvelopeFaker.CreateMemberSecret(
                organization.Id,
                vault.Id,
                entryId,
                revision: 1,
                operation: EntryOperation.Created,
                seed: 128));
        var replacement = VaultEntryVersion.Create(
            replacementSecret,
            new AllocatedVaultSequences(new MemberSequence(99), null),
            memberIndexChanged: true,
            apiFactory.FakeClock.GetCurrentInstant(),
            ActorType.Member,
            user.Id);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.EntryVersions.Add(replacement);

        await Should.ThrowAsync<DbUpdateException>(() => writeContext.SaveChangesAsync());
    }

    [Fact]
    public async Task When_MemberListsEntries_Then_ReturnsOnlyEncryptedMemberIndexes()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        await CreateEntryAsync(client, organization.Id, vault.Id);
        await CreateEntryAsync(client, organization.Id, vault.Id, seed: 96);

        var (response, result) = await client
            .GETAsync<ListEntriesEndpoint, ListEntriesRequest, ListEntriesResponse>(
                new ListEntriesRequest { VaultId = vault.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.Count.ShouldBe(2);
        result.Items.ShouldAllBe(item => item.MemberIndex.EncodedSuitePayload.Length > 0);
        typeof(EntryListItem).GetProperty("Label").ShouldBeNull();
        typeof(EntryListItem).GetProperty("UrlDomain").ShouldBeNull();
    }

    [Fact]
    public async Task When_UserMissingVaultManageCreatesEntry_Then_Returns403()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var privilegedClient = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await ReserveEntryIdAsync(privilegedClient, vault.Id);
        var restrictedClient = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        var (response, _) = await restrictedClient
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(
                EntryEnvelopeFaker.CreateRequest(organization.Id, vault.Id, entryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<Guid> CreateEntryAsync(
        HttpClient client,
        Guid organizationId,
        Guid vaultId,
        int seed = 0)
    {
        var entryId = await ReserveEntryIdAsync(client, vaultId);
        var (response, _) = await client
            .POSTAsync<CreateEntryEndpoint, CreateEntryRequest, CreateEntryResponse>(
                EntryEnvelopeFaker.CreateRequest(organizationId, vaultId, entryId, seed: seed));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return entryId;
    }

    private static async Task<Guid> ReserveEntryIdAsync(HttpClient client, Guid vaultId)
    {
        var (response, result) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = vaultId,
            });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return result!.Items.Single().EntryId;
    }
}
