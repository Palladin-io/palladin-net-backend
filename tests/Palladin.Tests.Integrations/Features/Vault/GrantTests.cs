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
    [Fact]
    public async Task ProactiveGranularGrant_PersistsDurableScopeAndSecretEnvelope()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);

        var (response, result) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

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
            GrantEntries =
            [
                staleMemberGeneration
                    ? request.GrantEntries.Single() with
                    {
                        Descriptor = request.GrantEntries.Single().Descriptor with { MemberKeyGeneration = 2 },
                    }
                    : request.GrantEntries.Single() with
                    {
                        Descriptor = request.GrantEntries.Single().Descriptor with
                        {
                            Binding = request.GrantEntries.Single().Descriptor.Binding with { RecipientKeyVersion = 2 },
                        },
                    },
            ],
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

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
            GrantEntries = [request.GrantEntries.Single() with
            {
                Descriptor = request.GrantEntries.Single().Descriptor with
                {
                    Scope = request.GrantEntries.Single().Descriptor.Scope with { OrganizationId = Guid.NewGuid() },
                },
            }],
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProactiveGrant_WithStaleEntryRevision_ReturnsConflict()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntries = [request.GrantEntries.Single() with
            {
                Descriptor = request.GrantEntries.Single().Descriptor with
                {
                    Binding = request.GrantEntries.Single().Descriptor.Binding with { EntryRevision = "2" },
                },
            }],
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ProactiveGrant_WithOversizedEncodedEnvelope_FailsBeforeDecoding()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            GrantEntries =
            [
                request.GrantEntries.Single() with
                {
                    EncodedSuitePayload = new string('A', ((262_144 + 24 + 2) / 3) * 4 + 1),
                },
            ],
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

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
            GrantEntries = [request.GrantEntries.Single() with
            {
                Descriptor = request.GrantEntries.Single().Descriptor with
                {
                    Binding = request.GrantEntries.Single().Descriptor.Binding with { ExpiresAt = expiresAt },
                },
            }],
        };

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoke_HardDeletesEnvelopeAndKeepsScope()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, created) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

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
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(Request(setup));

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(Request(setup));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TerminalGrantIdRetry_ReturnsConflictInsteadOfPersistenceFailure()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, created) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);
        await setup.Client.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = setup.VaultId, GrantId = created!.Id });

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(fullRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.GrantEntryEnvelopes.AnyAsync(x => x.GrantId == oldGrant.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task ProactiveFullGrant_WithMultipleCurrentEntries_CreatesSingleVaultKeyWrapper()
    {
        var setup = await ArrangeAsync();
        await apiFactory.Services.SeedEntryAsync(setup.VaultId, Guid.NewGuid());
        var request = FullRequest(setup);

        var (response, _) = await setup.Client
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.AnyAsync(g => g.Id == request.GrantId)).ShouldBeTrue();
        (await db.AgentWrappedVaultKeys.CountAsync(x => x.GrantId == request.GrantId)).ShouldBe(1);
        (await db.GrantEntryScopes.AnyAsync(x => x.GrantId == request.GrantId)).ShouldBeFalse();
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
            .POSTAsync<CreateGrantEndpoint, CreateGrantRequest, CreateGrantResponse>(Request(setup));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        createTask.IsCompleted.ShouldBeFalse();

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        var (response, _) = await createTask;
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
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

    private static CreateGrantRequest Request(Setup setup)
    {
        var grantId = Guid.NewGuid();
        return new CreateGrantRequest
        {
            GrantId = grantId,
            VaultId = setup.VaultId,
            AgentId = setup.AgentId,
            Type = GrantType.Granular,
            EntryId = setup.EntryId,
            Methods = GrantMethods.Get,
            GrantEntries =
            [
                GrantEnvelopeTestData.Contract(
                    setup.OrganizationId, setup.VaultId, grantId, setup.EntryId, setup.AgentPublicKey,
                    agentId: setup.AgentId),
            ],
        };
    }

    private static CreateGrantRequest FullRequest(Setup setup)
    {
        var request = Request(setup);
        return request with
        {
            Type = GrantType.Full,
            EntryId = null,
            GrantEntries = [],
            AgentWrappedVaultKey = AgentWrappedVaultKeyContractMapper.ToContract(
                GrantEnvelopeTestData.AgentVaultKey(
                    setup.OrganizationId,
                    setup.VaultId,
                    request.GrantId,
                    setup.AgentId,
                    agentPublicKey: setup.AgentPublicKey)),
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
