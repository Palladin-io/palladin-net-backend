using System.Net;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
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
public sealed class GrantTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(GrantFieldSelectionMode.All)]
    [InlineData(GrantFieldSelectionMode.Selected)]
    public async Task ProactiveGranularGrant_PersistsDurableScopeAndSecretEnvelope(GrantFieldSelectionMode selectionMode)
    {
        var setup = await ArrangeAsync();
        var request = Request(setup) with { FieldSelectionMode = selectionMode };

        var (response, result) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants.Include(g => g.GrantEntryScopes).ThenInclude(s => s.Envelope)
            .SingleAsync(g => g.Id == result!.Id);
        var envelope = grant.GrantEntryScopes.Single().Envelope.ShouldNotBeNull();
        envelope.MemberKeyGeneration.Value.ShouldBe(1u);
        envelope.RecipientAgentKeyVersion.Value.ShouldBe(1u);

        var (getResponse, contract) = await setup.Client.GETAsync<
            GetGrantEndpoint,
            GetGrantRequest,
            GrantResponse>(new GetGrantRequest { VaultId = setup.VaultId, GrantId = result.Id });
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        contract!.RecipientAgentKeyVersion.ShouldBe(1u);
        var exposedScope = contract.EntryScopes.Single();
        exposedScope.EntryId.ShouldBe(setup.EntryId);
        exposedScope.FieldIds.ShouldBe(["password", "username"]);
        exposedScope.FieldSelectionMode.ShouldBe(selectionMode);
        exposedScope.SelectedFieldIds.ShouldBe(selectionMode == GrantFieldSelectionMode.Selected
            ? ["password", "username"] : []);
        exposedScope.GrantEnvelopeRevision.ShouldBe("1");
        exposedScope.EntryRevision.ShouldBe("1");
        exposedScope.MemberKeyGeneration.ShouldBe(1u);
        exposedScope.RecipientAgentKeyVersion.ShouldBe(1u);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProactiveGrant_WithStaleAadKeyContext_FailsClosed(bool staleMemberGeneration)
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntry = staleMemberGeneration
                    ? request.GrantEntry with
                    {
                        Descriptor = request.GrantEntry.Descriptor with { MemberKeyGeneration = 2 },
                    }
                    : request.GrantEntry with
                    {
                        Descriptor = request.GrantEntry.Descriptor with
                        {
                            Binding = request.GrantEntry.Descriptor.Binding with { RecipientKeyVersion = 2 },
                        },
                    },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(g => g.Id == request.GrantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task ProactiveGrant_WithCrossTenantEnvelope_FailsClosed()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntry = request.GrantEntry with
            {
                Descriptor = request.GrantEntry.Descriptor with
                {
                    Scope = request.GrantEntry.Descriptor.Scope with { OrganizationId = Guid.NewGuid() },
                },
            },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProactiveGrant_WithStaleEntryRevision_ReturnsConflict()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntry = request.GrantEntry with
            {
                Descriptor = request.GrantEntry.Descriptor with
                {
                    Binding = request.GrantEntry.Descriptor.Binding with { EntryRevision = "2" },
                },
            },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ProactiveGrant_WithOversizedEncodedEnvelope_FailsBeforeDecoding()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntry = request.GrantEntry with
                {
                    EncodedSuitePayload = new string('A', ((262_144 + 24 + 2) / 3) * 4 + 1),
                },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProactiveGrant_WithNonCanonicalExpiryPrecision_FailsClosed()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var canonicalExpiry = PostgreSqlInstant.Normalize(
            apiFactory.FakeClock.GetCurrentInstant().Plus(Duration.FromHours(1)));
        var expiresAt = Instant.FromUnixTimeTicks(canonicalExpiry.ToUnixTimeTicks() + 1);
        request = request with
        {
            ExpiresAt = expiresAt,
            GrantEntry = request.GrantEntry with
            {
                Descriptor = request.GrantEntry.Descriptor with
                {
                    Binding = request.GrantEntry.Descriptor.Binding with { ExpiresAt = expiresAt },
                },
            },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoke_HardDeletesEnvelopeAndKeepsScope()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, created) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        var response = await setup.Client.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = setup.VaultId, GrantId = created!.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.GrantEntryScopes.CountAsync(s => s.GrantId == created.Id)).ShouldBe(1);
        (await db.GrantEntryEnvelopes.AnyAsync(e => e.GrantId == created.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task DuplicateActiveCoverage_ReturnsConflict()
    {
        var setup = await ArrangeAsync();
        var (_, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(Request(setup));

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(Request(setup));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TerminalGrantIdRetry_ReturnsConflictInsteadOfPersistenceFailure()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, created) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);
        await setup.Client.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = setup.VaultId, GrantId = created!.Id });

        var (response, _) = await setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task FullGrant_WithPreviouslyUsedPrimaryKey_ReturnsConflict()
    {
        var setup = await ArrangeAsync();
        var request = FullRequest(setup);
        var terminal = GrantFaker.CreateFull(
            id: request.GrantId,
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            status: GrantStatus.Revoked).Generate();
        await apiFactory.Services.SeedFullGrantAsync(terminal);
        var granular = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId).Generate();
        granular.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId, setup.VaultId, granular.Id, setup.EntryId, granular.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(granular);

        var (response, _) = await setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.SingleAsync(grant => grant.Id == granular.Id)).Status.ShouldBe(GrantStatus.Active);
        (await db.GrantEntryEnvelopes.AnyAsync(envelope => envelope.GrantId == granular.Id)).ShouldBeTrue();
        (await db.AgentWrappedVaultKeys.AnyAsync(wrapper => wrapper.GrantId == request.GrantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task FullGrantSupersede_HardDeletesOldGranularEnvelope()
    {
        var setup = await ArrangeAsync();
        var oldGrant = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId).Generate();
        oldGrant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId, setup.VaultId, oldGrant.Id, setup.EntryId, oldGrant.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(oldGrant);
        var fullRequest = FullRequest(setup);

        var (response, _) = await setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(fullRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.GrantEntryEnvelopes.AnyAsync(x => x.GrantId == oldGrant.Id)).ShouldBeFalse();
        var superseded = await db.Grants.SingleAsync(x => x.Id == oldGrant.Id);
        superseded.Status.ShouldBe(GrantStatus.Superseded);
        superseded.SupersededByGrantId.ShouldBe(fullRequest.GrantId);
        superseded.SupersededAt.ShouldNotBeNull();

        var (getResponse, contract) = await setup.Client.GETAsync<
            GetGrantEndpoint,
            GetGrantRequest,
            GrantResponse>(new GetGrantRequest { VaultId = setup.VaultId, GrantId = oldGrant.Id });
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        contract!.SupersededByGrantId.ShouldBe(fullRequest.GrantId);
        contract.SupersededAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task FullGrantSupersede_PagesLargeGranularHistoryAtomically()
    {
        var setup = await ArrangeAsync();
        var granular = Enumerable.Range(0, 101)
            .Select(_ => GrantFaker.CreateGranular(
                vaultId: setup.VaultId,
                organizationId: setup.OrganizationId,
                agentId: setup.AgentId,
                entryId: setup.EntryId).Generate())
            .ToArray();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            writeContext.AddRange(granular);
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        var request = FullRequest(setup);
        var (response, _) = await setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.CountAsync(g => granular.Select(item => item.Id).Contains(g.Id)
                                         && g.Status == GrantStatus.Superseded
                                         && g.SupersededByGrantId == request.GrantId)).ShouldBe(101);
        (await db.AgentWrappedVaultKeys.CountAsync(x => x.GrantId == request.GrantId)).ShouldBe(1);
    }

    [Fact]
    public async Task ProactiveFullGrant_WithMultipleCurrentEntries_CreatesSingleVaultKeyWrapper()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntryAsync(setup.VaultId, Guid.NewGuid());
        var request = FullRequest(setup);

        var (response, _) = await setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(g => g.Id == request.GrantId)).ShouldBeTrue();
        (await db.AgentWrappedVaultKeys.CountAsync(x => x.GrantId == request.GrantId)).ShouldBe(1);
        (await db.GrantEntryScopes.AnyAsync(x => x.GrantId == request.GrantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task ProactiveFullGrant_WithForgedProducerSignature_FailsBeforePersistence()
    {
        var setup = await ArrangeAsync();
        var request = FullRequest(setup);
        request = request with
        {
            AgentWrappedVaultKey = request.AgentWrappedVaultKey with
            {
                ProducerSignature = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(new byte[64]),
            },
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(g => g.Id == request.GrantId)).ShouldBeFalse();
        (await db.AgentWrappedVaultKeys.AnyAsync(x => x.GrantId == request.GrantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task TerminalGranularGrant_WithOnlyStaleActiveFullMaterial_CanBeGrantedAgain()
    {
        var setup = await ArrangeAsync();
        await using (var sequenceScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = sequenceScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.SingleAsync(x => x.Id == setup.VaultId);
            vault.AllocateSequences(false, Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }
        var update = EntryEnvelopeFaker.CreateUpdateRequest(
            setup.OrganizationId,
            setup.VaultId,
            setup.EntryId,
            baseRevision: 1);
        var (updateResponse, _) = await setup.Client
            .PUTAsync<UpdateEntryEndpoint, UpdateEntryRequest, UpdateEntryResponse>(update);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var terminal = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Revoked).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(terminal);
        var staleFull = GrantFaker.CreateFull(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId).Generate();
        staleFull.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId,
            setup.VaultId,
            staleFull.Id,
            setup.EntryId,
            staleFull.Methods,
            staleFull.ExpiresAt,
            staleFull.QueryLimit,
            entryRevision: 1));
        await apiFactory.Services.SeedFullGrantAsync(staleFull);

        var response = await setup.Client.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{terminal.Id}");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.RootElement.GetProperty("canGrantAgain").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("activeCoveringGrantIds").GetArrayLength().ShouldBe(0);

        var listResponse = await setup.Client.GetAsync($"api/vaults/{setup.VaultId}/grants");
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listedTerminal = listJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == terminal.Id);

        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        listedTerminal.GetProperty("canGrantAgain").GetBoolean().ShouldBeTrue();
        listedTerminal.GetProperty("activeCoveringGrantIds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task TerminalGranularGrant_WithCurrentActiveCoverage_ReturnsCoveringGrantId()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await ArrangeAsync();
        var terminal = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Revoked).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(terminal);
        var active = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId).Generate();
        active.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId, setup.VaultId, active.Id, setup.EntryId, active.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(active);

        var response = await setup.Client.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{terminal.Id}", ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.RootElement.GetProperty("canGrantAgain").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("activeCoveringGrantIds").EnumerateArray()
            .Single().GetGuid().ShouldBe(active.Id);

        var listResponse = await setup.Client.GetAsync($"api/vaults/{setup.VaultId}/grants", ct);
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct));
        var listedTerminal = listJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == terminal.Id);

        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        listedTerminal.GetProperty("canGrantAgain").GetBoolean().ShouldBeFalse();
        listedTerminal.GetProperty("activeCoveringGrantIds").EnumerateArray()
            .Single().GetGuid().ShouldBe(active.Id);
    }

    [Fact]
    public async Task TerminalGranularGrant_WithOnlyPreviousEpochCoverage_CanBeGrantedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await ArrangeAsync(agentAccessEpoch: 2);
        var terminal = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Revoked,
            agentAccessEpoch: 1).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(terminal);
        var stale = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            agentAccessEpoch: 1).Generate();
        stale.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId, setup.VaultId, stale.Id, setup.EntryId, stale.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(stale);

        var response = await setup.Client.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{terminal.Id}", ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.RootElement.GetProperty("canGrantAgain").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("activeCoveringGrantIds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task TerminalFullGrant_ForDeletingVault_CannotBeGrantedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await ArrangeAsync();
        var terminal = GrantFaker.CreateFull(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            status: GrantStatus.Revoked).Generate();
        terminal.AgentWrappedVaultKey.ShouldBeNull();
        await apiFactory.Services.SeedFullGrantAsync(terminal);
        var active = GrantFaker.CreateFull(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId).Generate();
        await apiFactory.Services.SeedFullGrantAsync(active);
        await using (var deletionScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = deletionScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.Include(value => value.VaultMembers)
                .SingleAsync(value => value.Id == setup.VaultId, ct);
            vault.BeginDeletion(Guid.NewGuid(), "test actor", apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(ct);
        }

        var response = await setup.Client.GetAsync($"api/grants?vaultId={setup.VaultId}", ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var listedTerminal = json.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == terminal.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        listedTerminal.GetProperty("canGrantAgain").GetBoolean().ShouldBeFalse();
        listedTerminal.GetProperty("activeCoveringGrantIds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task TerminalGrant_ForInactiveAgent_CannotBeGrantedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await ArrangeAsync(AgentStatus.Deactivated);
        var terminal = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Revoked).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(terminal);

        var response = await setup.Client.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{terminal.Id}", ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.RootElement.GetProperty("canGrantAgain").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("activeCoveringGrantIds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task CreateGrant_WaitsForStableVaultLifecycleLock()
    {
        var setup = await ArrangeAsync();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await writeContext.LockVault(setup.OrganizationId, setup.VaultId)
            .SingleAsync(TestContext.Current.CancellationToken);

        var createTask = setup.Client
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(Request(setup));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        createTask.IsCompleted.ShouldBeFalse();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var (response, _) = await createTask;
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateFullGrant_WithAgentKeyChangeBeforeLock_ReturnsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await ArrangeAsync();
        var request = FullRequest(setup);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        var lockedAgent = await writeContext.LockAgent(setup.OrganizationId, setup.AgentId).SingleAsync(ct);

        var createTask = setup.Client
            .POSTAsync<CreateFullGrantEndpoint, CreateFullGrantRequest, CreateFullGrantResponse>(request);
        await WaitForBlockedAgentLockAsync(ct);

        lockedAgent.Apply(
            lockedAgent.Status,
            Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray()),
            checked(lockedAgent.RecipientKeyVersion + 1),
            lockedAgent.SigningPublicKey,
            lockedAgent.Name,
            lockedAgent.IconKey,
            lockedAgent.IconColor,
            checked(lockedAgent.AccessEpoch + 1),
            lockedAgent.UpdatedAt + Duration.FromSeconds(1),
            lockedAgent.UpdatedAt + Duration.FromSeconds(1));
        await writeContext.CommitAsync(transaction, ct);

        var (response, _) = await createTask;
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var db = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(grant => grant.Id == request.GrantId, ct)).ShouldBeFalse();
    }

    private async Task WaitForBlockedAgentLockAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            var isBlocked = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%FROM "Agents"%'
                      AND query LIKE '%FOR UPDATE%'
                ) AS "Value"
                """).SingleAsync(ct);
            if (isBlocked)
            {
                return;
            }

            await Task.Delay(25, ct);
        }

        throw new TimeoutException("FULL grant creation did not reach the blocked Agent row lock.");
    }

    private async Task<Setup> ArrangeAsync(
        AgentStatus agentStatus = AgentStatus.Active,
        uint? agentAccessEpoch = null)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id, agentStatus, accessEpoch: agentAccessEpoch);
        return new Setup(
            apiFactory.CreateAuthenticatedClient(user), organization.Id, vault.Id, entry.Id,
            agent.Id, agent.PublicKey);
    }

    private static CreateGranularGrantRequest Request(Setup setup)
    {
        var grantId = Guid.NewGuid();
        return new CreateGranularGrantRequest
        {
            GrantId = grantId,
            VaultId = setup.VaultId,
            AgentId = setup.AgentId,
            EntryId = setup.EntryId,
            Methods = GrantMethods.Get,
            GrantEntry = GrantEnvelopeTestData.Contract(
                setup.OrganizationId, setup.VaultId, grantId, setup.EntryId, setup.AgentPublicKey,
                agentId: setup.AgentId),
        };
    }

    private static CreateFullGrantRequest FullRequest(Setup setup)
    {
        var grantId = Guid.NewGuid();
        return new CreateFullGrantRequest
        {
            GrantId = grantId,
            VaultId = setup.VaultId,
            AgentId = setup.AgentId,
            Methods = GrantMethods.Get,
            AgentWrappedVaultKey = GrantEnvelopeTestData.AgentVaultKeyContract(
                setup.OrganizationId,
                setup.VaultId,
                grantId,
                setup.AgentId,
                agentPublicKey: setup.AgentPublicKey),
        };
    }

    private sealed record Setup(
        HttpClient Client,
        Guid OrganizationId,
        Guid VaultId,
        Guid EntryId,
        Guid AgentId,
        string AgentPublicKey);
}
