using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class FullGrantPreparationTests(ApiFactory apiFactory) : TestBase
{
    private const int QueryLimit = 5;
    private const GrantMethods Methods = GrantMethods.Get | GrantMethods.Exec | GrantMethods.Inject;

    [Theory]
    [InlineData(GrantMethods.Get)]
    [InlineData(GrantMethods.Exec)]
    [InlineData(GrantMethods.Inject)]
    [InlineData(GrantMethods.Get | GrantMethods.Exec | GrantMethods.Inject)]
    public async Task StandardEnvelope_AcceptsEveryUserSelectedMethodSet(GrantMethods methods)
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 1);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId, methods);
        var (_, material) = await GetMaterialAsync(setup, grantId);
        var item = material!.Items.Single();
        var (appendResponse, _) = await setup.Client.PUTAsync<
            AppendFullGrantPreparationEntriesEndpoint,
            AppendFullGrantPreparationEntriesRequest,
            AppendFullGrantPreparationEntriesResponse>(new AppendFullGrantPreparationEntriesRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
                GrantEntries =
                [
                    GrantEnvelopeTestData.Contract(
                        setup.OrganizationId,
                        setup.VaultId,
                        grantId,
                        item.EntryId,
                        setup.AgentPublicKey,
                        remainingUses: QueryLimit,
                        entryRevision: ulong.Parse(item.EntryRevision),
                        agentId: setup.AgentId,
                        methods: methods,
                        deliveryPolicy: GrantDeliveryPolicy.Standard),
                ],
            });
        var (commitResponse, _) = await setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });

        appendResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        commitResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task MoreThanFiveHundredEntries_ArePreparedInBoundedPages_AndCommittedAtomically()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 501);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);

        var (entryIds, materialPages) = await AppendAllAsync(setup, grantId);

        entryIds.Count.ShouldBe(501);
        materialPages.ShouldBe(6);
        await AssertNoGrantAsync(grantId);

        var (commitResponse, created) = await setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });

        commitResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        created!.Id.ShouldBe(grantId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants.OfType<FullGrant>()
            .Include(x => x.GrantEntryScopes).ThenInclude(x => x.Envelope)
            .SingleAsync(x => x.Id == grantId);
        grant.Status.ShouldBe(GrantStatus.Active);
        grant.GrantEntryScopes.Count.ShouldBe(501);
        grant.GrantEntryScopes.ShouldAllBe(x => x.Envelope != null);
        (await db.FullGrantPreparations.AnyAsync(x => x.Id == grantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidPayloadOnLaterPreparedPage_RollsBackEveryGrantWrite()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 101);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        await AppendAllAsync(setup, grantId);
        await using (var corruptionScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeDb = corruptionScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var laterPageEntryId = await writeDb.FullGrantPreparationEntries
                .Where(x => x.PreparationId == grantId)
                .OrderByDescending(x => x.EntryId)
                .Select(x => x.EntryId)
                .FirstAsync();
            await writeDb.FullGrantPreparationEntries
                .Where(x => x.PreparationId == grantId && x.EntryId == laterPageEntryId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.Payload, new byte[] { 0xff }),
                    TestContext.Current.CancellationToken);
        }

        var (response, _) = await setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertNoGrantAsync(grantId);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.FullGrantPreparationEntries.CountAsync(x => x.PreparationId == grantId)).ShouldBe(101);
    }

    [Fact]
    public async Task EntryChangedAfterAppend_MakesCommitFailWithoutActiveGrant()
    {
        var setup = await ArrangeAsync();
        var entries = await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 1);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        await AppendAllAsync(setup, grantId);
        var (updateResponse, _) = await setup.Client.PUTAsync<
            UpdateEntryEndpoint,
            UpdateEntryRequest,
            UpdateEntryResponse>(EntryEnvelopeFaker.CreateUpdateRequest(
            setup.OrganizationId,
            setup.VaultId,
            entries.Single().Id,
            baseRevision: 1));
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (commitResponse, _) = await setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });

        commitResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertNoGrantAsync(grantId);
    }

    [Fact]
    public async Task MaterialPageWithMissingCanonicalHead_ReturnsConflictInsteadOfPartialMaterial()
    {
        var setup = await ArrangeAsync();
        var entry = (await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 1)).Single();
        await using (var corruptionScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeDb = corruptionScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            await writeDb.EntryVersions
                .Where(x => x.OrganizationId == setup.OrganizationId
                            && x.VaultId == setup.VaultId
                            && x.EntryId == entry.Id)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);

        var response = await setup.Client.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/full/preparations/{grantId}/material?pageSize=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentEncoding.ShouldContain("identity");
        await AssertNoGrantAsync(grantId);
    }

    [Fact]
    public async Task EmptyVault_CommitsActiveFullGrant_AndFutureEntryMustMaterializeItsEnvelope()
    {
        var setup = await ArrangeAsync();
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        var (materialResponse, material) = await GetMaterialAsync(setup, grantId);
        materialResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        material!.Items.ShouldBeEmpty();
        material.NextAfterEntryId.ShouldBeNull();

        var (commitResponse, _) = await setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });
        commitResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using (var emptyGrantScope = apiFactory.Services.CreateAsyncScope())
        {
            var db = emptyGrantScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var emptyGrant = await db.Grants.OfType<FullGrant>()
                .Include(x => x.GrantEntryScopes)
                .SingleAsync(x => x.Id == grantId);
            emptyGrant.Status.ShouldBe(GrantStatus.Active);
            emptyGrant.GrantEntryScopes.ShouldBeEmpty();
        }

        var (challengeResponse, challenge) = await setup.Client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest
            {
                VaultId = setup.VaultId,
            });
        challengeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entryId = challenge!.Items.Single().EntryId;
        var request = EntryEnvelopeFaker.CreateRequest(setup.OrganizationId, setup.VaultId, entryId) with
        {
            GrantEnvelopes =
            [
                GrantEnvelopeTestData.Contract(
                    setup.OrganizationId,
                    setup.VaultId,
                    grantId,
                    entryId,
                    setup.AgentPublicKey,
                    remainingUses: QueryLimit,
                    agentId: setup.AgentId,
                    methods: Methods),
            ],
        };
        var (createResponse, _) = await setup.Client.POSTAsync<
            CreateEntryEndpoint,
            CreateEntryRequest,
            CreateEntryResponse>(request);

        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readDb = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var scope = await readDb.GrantEntryScopes.Include(x => x.Envelope)
            .SingleAsync(x => x.GrantId == grantId && x.EntryId == entryId);
        scope.Envelope.ShouldNotBeNull().EntryRevision.ShouldBe(1UL);
    }

    [Fact]
    public async Task ExactAppendRetry_IsIdempotent_AndChangedRetryConflicts()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 1);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        var (_, material) = await GetMaterialAsync(setup, grantId);
        var item = material!.Items.Single();
        var append = AppendRequest(setup, grantId, item);

        var (firstResponse, first) = await setup.Client.PUTAsync<
            AppendFullGrantPreparationEntriesEndpoint,
            AppendFullGrantPreparationEntriesRequest,
            AppendFullGrantPreparationEntriesResponse>(append);
        var (retryResponse, retry) = await setup.Client.PUTAsync<
            AppendFullGrantPreparationEntriesEndpoint,
            AppendFullGrantPreparationEntriesRequest,
            AppendFullGrantPreparationEntriesResponse>(append);
        var changed = append with
        {
            GrantEntries =
            [
                GrantEnvelopeTestData.Contract(
                    setup.OrganizationId,
                    setup.VaultId,
                    grantId,
                    item.EntryId,
                    setup.AgentPublicKey,
                    remainingUses: QueryLimit,
                    entryRevision: ulong.Parse(item.EntryRevision),
                    fieldIds: ["changed"],
                    agentId: setup.AgentId,
                    methods: Methods),
            ],
        };
        var (changedResponse, _) = await setup.Client.PUTAsync<
            AppendFullGrantPreparationEntriesEndpoint,
            AppendFullGrantPreparationEntriesRequest,
            AppendFullGrantPreparationEntriesResponse>(changed);

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        first!.AcceptedEntries.ShouldBe(1);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry!.AcceptedEntries.ShouldBe(0);
        retry.TotalPreparedEntries.ShouldBe(1);
        changedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertNoGrantAsync(grantId);
    }

    [Fact]
    public async Task ReusedPreparationIdForDifferentAgent_ReturnsConflict()
    {
        var setup = await ArrangeAsync();
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        var secondAgent = await apiFactory.Services.SeedVaultAgentAsync(setup.OrganizationId);

        var (response, _) = await setup.Client.POSTAsync<
            StartFullGrantPreparationEndpoint,
            StartFullGrantPreparationRequest,
            StartFullGrantPreparationResponse>(new StartFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
                AgentId = secondAgent.Id,
                Methods = Methods,
                QueryLimit = QueryLimit,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ExpiredPreparationCleanup_RemovesPreparationAndStagedEntries()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntriesAsync(setup.VaultId, setup.UserId, 1);
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        await AppendAllAsync(setup, grantId);
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            apiFactory.FakeClock.Advance(NodaTime.Duration.FromMinutes(16));
            await using var jobScope = apiFactory.Services.CreateAsyncScope();
            var job = jobScope.ServiceProvider.GetRequiredService<CleanupFullGrantPreparationsJob>();
            await job.ExecuteAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }

        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.FullGrantPreparations.AnyAsync(x => x.Id == grantId)).ShouldBeFalse();
        (await db.FullGrantPreparationEntries.AnyAsync(x => x.PreparationId == grantId)).ShouldBeFalse();
        (await db.Grants.AnyAsync(x => x.Id == grantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Commit_WaitsForOrganizationAgentLifecycleFence()
    {
        var setup = await ArrangeAsync();
        var grantId = Guid.NewGuid();
        await StartAsync(setup, grantId);
        await using var lockScope = apiFactory.Services.CreateAsyncScope();
        var writeContext = lockScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await writeContext.LockOrganizationAgentLifecycle(setup.OrganizationId)
            .SingleAsync(TestContext.Current.CancellationToken);

        var commitTask = setup.Client.POSTAsync<
            CommitFullGrantPreparationEndpoint,
            CommitFullGrantPreparationRequest,
            CreateGrantResponse>(new CommitFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
            });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        commitTask.IsCompleted.ShouldBeFalse();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var (response, _) = await commitTask;
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<Setup> ArrangeAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        return new Setup(
            apiFactory.CreateAuthenticatedClient(user),
            user.Id,
            organization.Id,
            vault.Id,
            agent.Id,
            agent.PublicKey);
    }

    private async Task StartAsync(Setup setup, Guid grantId, GrantMethods methods = Methods)
    {
        var (response, preparation) = await setup.Client.POSTAsync<
            StartFullGrantPreparationEndpoint,
            StartFullGrantPreparationRequest,
            StartFullGrantPreparationResponse>(new StartFullGrantPreparationRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
                AgentId = setup.AgentId,
                Methods = methods,
                QueryLimit = QueryLimit,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        preparation!.OrganizationId.ShouldBe(setup.OrganizationId);
        preparation.GrantId.ShouldBe(grantId);
        preparation.AgentAccessEpoch.ShouldBe(1u);
        preparation.MemberKeyGeneration.ShouldBe(1u);
        preparation.RecipientAgentKeyVersion.ShouldBe(1u);
        preparation.AgentKeyFingerprint.ShouldNotContain("=");
        preparation.AgentPublicKey.ShouldBe(setup.AgentPublicKey);
    }

    private static async Task<(HttpResponseMessage Response, GetFullGrantPreparationMaterialResponse? Material)>
        GetMaterialAsync(Setup setup, Guid grantId, Guid? afterEntryId = null)
    {
        var (response, material) = await setup.Client.GETAsync<
            GetFullGrantPreparationMaterialEndpoint,
            GetFullGrantPreparationMaterialRequest,
            GetFullGrantPreparationMaterialResponse>(new GetFullGrantPreparationMaterialRequest
            {
                VaultId = setup.VaultId,
                GrantId = grantId,
                AfterEntryId = afterEntryId,
                PageSize = 100,
            });
        response.Content.Headers.ContentEncoding.ShouldContain("identity");
        return (response, material);
    }

    private async Task<(IReadOnlyList<Guid> EntryIds, int MaterialPages)> AppendAllAsync(
        Setup setup,
        Guid grantId)
    {
        var entryIds = new List<Guid>();
        Guid? cursor = null;
        var pages = 0;
        do
        {
            var (materialResponse, material) = await GetMaterialAsync(setup, grantId, cursor);
            materialResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            material.ShouldNotBeNull();
            material.Items.Count.ShouldBeLessThanOrEqualTo(100);
            pages++;
            if (material.Items.Count > 0)
            {
                var appendRequest = new AppendFullGrantPreparationEntriesRequest
                {
                    VaultId = setup.VaultId,
                    GrantId = grantId,
                    GrantEntries = material.Items.Select(item => Contract(setup, grantId, item)).ToArray(),
                };
                var (appendResponse, appended) = await setup.Client.PUTAsync<
                    AppendFullGrantPreparationEntriesEndpoint,
                    AppendFullGrantPreparationEntriesRequest,
                    AppendFullGrantPreparationEntriesResponse>(appendRequest);
                appendResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
                appended!.AcceptedEntries.ShouldBe(material.Items.Count);
                entryIds.AddRange(material.Items.Select(x => x.EntryId));
            }

            cursor = material.NextAfterEntryId;
        } while (cursor.HasValue);

        entryIds.Distinct().Count().ShouldBe(entryIds.Count);
        return (entryIds, pages);
    }

    private static AppendFullGrantPreparationEntriesRequest AppendRequest(
        Setup setup,
        Guid grantId,
        FullGrantPreparationMaterialItem item) => new()
        {
            VaultId = setup.VaultId,
            GrantId = grantId,
            GrantEntries = [Contract(setup, grantId, item)],
        };

    private static GrantEntryEnvelopeContract Contract(
        Setup setup,
        Guid grantId,
        FullGrantPreparationMaterialItem item) => GrantEnvelopeTestData.Contract(
        setup.OrganizationId,
        setup.VaultId,
        grantId,
        item.EntryId,
        setup.AgentPublicKey,
        remainingUses: QueryLimit,
        entryRevision: ulong.Parse(item.EntryRevision),
        agentId: setup.AgentId,
        methods: Methods);

    private async Task AssertNoGrantAsync(Guid grantId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(x => x.Id == grantId)).ShouldBeFalse();
        (await db.GrantEntryScopes.AnyAsync(x => x.GrantId == grantId)).ShouldBeFalse();
    }

    private sealed record Setup(
        HttpClient Client,
        Guid UserId,
        Guid OrganizationId,
        Guid VaultId,
        Guid AgentId,
        string AgentPublicKey);
}
