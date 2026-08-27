using System.Net;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class CredentialDeliveryTests(ApiFactory apiFactory) : TestBase
{
    private static readonly GrantNames TestNames = new("agent", "entry", "vault", "actor");

    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static GrantEntryScope Material(
        Guid orgId,
        Guid vaultId,
        Guid grantId,
        Guid entryId,
        int? remainingUses = null,
        GrantMethods methods = GrantMethods.Get,
        string[]? fieldIds = null,
        GrantDeliveryPolicy deliveryPolicy = GrantDeliveryPolicy.Standard) =>
        GrantEnvelopeTestData.Scope(
            orgId,
            vaultId,
            grantId,
            entryId,
            methods: methods,
            remainingUses: remainingUses,
            fieldIds: fieldIds,
            deliveryPolicy: deliveryPolicy);

    private async Task<(HttpClient AgentClient, Guid AgentId, Guid VaultId, Guid EntryId, Guid OrganizationId, Module.Identity.Domain.User User)> SetupAsync(
        uint authenticatedAccessEpoch = 1)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);

        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var authAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active)
                .RuleFor(x => x.AccessEpoch, authenticatedAccessEpoch));
        await apiFactory.Services.SeedVaultAgentAsync(organization.Id, status: AgentStatus.Active, id: authAgent.Id);

        var agentClient = apiFactory.CreateSignedAgentClient(authAgent.Id, plaintext, publicKey, signing);

        return (agentClient, authAgent.Id, vault.Id, entry.Id, organization.Id, user);
    }

    private static GranularGrant ActiveGranular(Guid vaultId, Guid orgId, Guid agentId, Guid entryId, Instant? expiresAt, int? queryLimit)
    {
        var grantId = Guid.NewGuid();
        return GranularGrant.CreateProactively(
            grantId, vaultId, orgId, agentId, "pk", entryId, Material(orgId, vaultId, grantId, entryId, queryLimit),
            expiresAt, queryLimit, expiresAt.HasValue ? "time" : "uses", GrantMethods.Get, Guid.NewGuid(),
            TestNames, SystemClock.Instance.GetCurrentInstant(), agentAccessEpoch: 1);
    }

    [Fact]
    public async Task When_GrantBelongsToPreviousAgentAccessEpoch_Then_DeliveryFailsClosed()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var replica = await writeContext.Agents.SingleAsync(a => a.Id == agentId);
            var reactivatedAt = apiFactory.FakeClock.GetCurrentInstant() + Duration.FromSeconds(1);
            replica.Apply(
                AgentStatus.Active,
                replica.PublicKey,
                replica.RecipientKeyVersion,
                replica.SigningPublicKey,
                replica.Name,
                replica.IconKey,
                replica.IconColor,
                accessEpoch: 2,
                accessEpochStartedAt: reactivatedAt,
                updatedAt: reactivatedAt);
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        // When
        var response = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}",
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
        persisted.QueryCount.ShouldBe(0);
        persisted.LastAccessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_AuthenticatedEpochAdvancesBeforeVaultReplica_Then_DeliveryFailsClosed()
    {
        // Given: Agents authenticates epoch 2 while Vault still projects epoch 1 and holds an epoch-1 grant.
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync(authenticatedAccessEpoch: 2);
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));

        // When
        var response = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}",
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
        persisted.QueryCount.ShouldBe(0);
        persisted.LastAccessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentHasActiveGranularGrant_Then_DeliversCiphertextMaterial()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var lower = body.ToLowerInvariant();
        lower.ShouldContain("encodedsuitepayload");
        lower.ShouldContain("wrappedgrantdek");
        lower.ShouldContain("resourcerevision");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var envelope = root.GetProperty("grantEnvelope");
        envelope.GetProperty("encodedSuitePayload").GetString()
            .ShouldBe(WebEncoders.Base64UrlEncode(Bytes(24 + 64)));
        envelope.GetProperty("wrappedGrantDek").GetProperty("encodedSealedKeyPackage").GetString()
            .ShouldBe(WebEncoders.Base64UrlEncode(Bytes(120)));
        envelope.GetProperty("descriptor").GetProperty("binding").GetProperty("recipientKeyFingerprint")
            .GetString().ShouldNotContain("=");
        envelope.GetProperty("descriptor").GetProperty("resourceRevision").GetString().ShouldBe("1");
        envelope.GetProperty("descriptor").GetProperty("binding").GetProperty("entryRevision").GetString()
            .ShouldBe("1");
        root.GetProperty("organizationId").GetGuid().ShouldBe(orgId);
        root.GetProperty("vaultId").GetGuid().ShouldBe(vaultId);
        root.GetProperty("agentId").GetGuid().ShouldBe(agentId);
        root.GetProperty("grantId").GetGuid().ShouldNotBe(Guid.Empty);
        root.GetProperty("approvedMethods").GetUInt16().ShouldBe((ushort)GrantMethods.Get);
        envelope.GetProperty("descriptor").GetProperty("memberKeyGeneration").GetUInt32().ShouldBe(1u);
        envelope.GetProperty("descriptor").GetProperty("binding").GetProperty("recipientKeyVersion")
            .GetUInt32().ShouldBe(1u);
        envelope.GetProperty("fieldIds").EnumerateArray().Select(x => x.GetString())
            .ShouldBe(["password", "username"]);
        envelope.GetProperty("descriptor").GetProperty("binding").GetProperty("remainingUses")
            .GetInt32().ShouldBe(5);
        envelope.GetProperty("descriptor").GetProperty("binding").GetProperty("expiresAt")
            .ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(GrantMethods.Get, HttpStatusCode.Forbidden, 0)]
    [InlineData(GrantMethods.Inject, HttpStatusCode.Forbidden, 0)]
    [InlineData(GrantMethods.Exec, HttpStatusCode.OK, 1)]
    public async Task When_GrantIsExecOnly_Then_OnlyExecCanDeliver(
        GrantMethods requestedMethod,
        HttpStatusCode expectedStatus,
        int expectedQueryCount)
    {
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grantId = Guid.NewGuid();
        var methods = GrantMethodsExtensions.All;
        var scope = Material(
            orgId,
            vaultId,
            grantId,
            entryId,
            remainingUses: 5,
            methods: methods,
            fieldIds: ["custom:script-source"],
            deliveryPolicy: GrantDeliveryPolicy.ExecOnly);
        var grant = GranularGrant.CreateProactively(
            grantId,
            vaultId,
            orgId,
            agentId,
            "pk",
            entryId,
            scope,
            expiresAt: null,
            queryLimit: 5,
            expirySource: "uses",
            methods,
            createdBy: Guid.NewGuid(),
            TestNames,
            SystemClock.Instance.GetCurrentInstant(),
            agentAccessEpoch: 1);
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var response = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}?method={requestedMethod}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expectedStatus);
        await using var scopeForAssertion = apiFactory.Services.CreateAsyncScope();
        var readContext = scopeForAssertion.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(x => x.Id == grantId);
        persisted.QueryCount.ShouldBe(expectedQueryCount);
    }

    [Theory]
    [InlineData(GrantMethods.Get, HttpStatusCode.Forbidden, 0)]
    [InlineData(GrantMethods.Exec, HttpStatusCode.Forbidden, 0)]
    [InlineData(GrantMethods.Inject, HttpStatusCode.OK, 1)]
    public async Task When_GrantIsInjectOnly_Then_OnlyInjectCanDeliver(
        GrantMethods requestedMethod,
        HttpStatusCode expectedStatus,
        int expectedQueryCount)
    {
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grantId = Guid.NewGuid();
        var methods = GrantMethodsExtensions.All;
        var scope = Material(
            orgId,
            vaultId,
            grantId,
            entryId,
            remainingUses: 5,
            methods: methods,
            fieldIds: ["cardNumber", "expiryMonth", "expiryYear"],
            deliveryPolicy: GrantDeliveryPolicy.InjectOnly);
        var grant = GranularGrant.CreateProactively(
            grantId,
            vaultId,
            orgId,
            agentId,
            "pk",
            entryId,
            scope,
            expiresAt: null,
            queryLimit: 5,
            expirySource: "uses",
            methods,
            createdBy: Guid.NewGuid(),
            TestNames,
            SystemClock.Instance.GetCurrentInstant(),
            agentAccessEpoch: 1);
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var response = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}?method={requestedMethod}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expectedStatus);
        await using var scopeForAssertion = apiFactory.Services.CreateAsyncScope();
        var readContext = scopeForAssertion.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(x => x.Id == grantId);
        persisted.QueryCount.ShouldBe(expectedQueryCount);
    }

    [Fact]
    public async Task When_StandardGrantUsesScriptLikeCustomFieldId_Then_GetIsNotBlocked()
    {
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grantId = Guid.NewGuid();
        var scope = Material(
            orgId,
            vaultId,
            grantId,
            entryId,
            remainingUses: 5,
            methods: GrantMethods.Get,
            fieldIds: ["script.custom"],
            deliveryPolicy: GrantDeliveryPolicy.Standard);
        var grant = GranularGrant.CreateProactively(
            grantId,
            vaultId,
            orgId,
            agentId,
            "pk",
            entryId,
            scope,
            expiresAt: null,
            queryLimit: 5,
            expirySource: "uses",
            GrantMethods.Get,
            createdBy: Guid.NewGuid(),
            TestNames,
            SystemClock.Instance.GetCurrentInstant(),
            agentAccessEpoch: 1);
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var response = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}?method={GrantMethods.Get}",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_EntryIsArchived_Then_ActiveGrantIsSuspendedWithoutBeingRevoked()
    {
        var (agentClient, agentId, vaultId, entryId, orgId, user) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));
        await using (var setupScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = setupScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.SingleAsync(x => x.Id == vaultId);
            vault.AllocateSequences(
                discoveryProjectionChanged: false,
                user.Id,
                apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        var (archiveResponse, _) = await memberClient.POSTAsync<
            ArchiveEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            orgId,
            vaultId,
            entryId,
            1,
            EntryOperation.Archived));

        var deliveryResponse = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}",
            TestContext.Current.CancellationToken);

        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        deliveryResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(x => x.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
        persisted.QueryCount.ShouldBe(0);
        persisted.LastAccessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_ArchivedEntryIsRestored_Then_PreArchiveEnvelopeRemainsNonDeliverable()
    {
        var (agentClient, agentId, vaultId, entryId, orgId, user) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));
        await using (var setupScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = setupScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.SingleAsync(x => x.Id == vaultId);
            vault.AllocateSequences(false, user.Id, apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        var memberClient = apiFactory.CreateAuthenticatedClient(user);
        var (archiveResponse, _) = await memberClient.POSTAsync<
            ArchiveEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            orgId, vaultId, entryId, 1, EntryOperation.Archived));
        var (restoreResponse, _) = await memberClient.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            orgId, vaultId, entryId, 2, EntryOperation.Restored, agentDiscoveryRevision: 1, seed: 64));

        var deliveryResponse = await agentClient.GetAsync(
            $"api/agent/vaults/{vaultId}/credentials/{entryId}",
            TestContext.Current.CancellationToken);

        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        deliveryResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(x => x.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
        persisted.QueryCount.ShouldBe(0);
        persisted.LastAccessedAt.ShouldBeNull();
        (await readContext.GrantEntryEnvelopes.SingleAsync(x => x.GrantId == grant.Id))
            .EntryRevision.ShouldBe(1UL);
    }

    [Fact]
    public async Task When_CredentialDelivered_Then_RecordsLastAccessOnGrant()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 5));
        agentClient.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentHostnameHeader, "agent-box-7");

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.LastAccessedAt.ShouldNotBeNull();
        persisted.LastAccessHostname.ShouldBe("agent-box-7");
    }

    [Fact]
    public async Task When_AgentHasActiveFullGrant_Then_DeliversForAnyCoveredEntry()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId, vaultId, orgId, agentId, "pk",
            GrantEnvelopeTestData.AgentVaultKey(orgId, vaultId, grantId, agentId),
            null, 3, "uses", GrantMethods.Get, Guid.NewGuid(), TestNames, SystemClock.Instance.GetCurrentInstant(), agentAccessEpoch: 1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_LifetimeGrant_Then_RepeatedRetrievesAlwaysGrantedAndStayActive()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: null));
        var url = $"api/agent/vaults/{vaultId}/credentials/{entryId}";

        // When
        var first = await agentClient.GetAsync(url);
        var second = await agentClient.GetAsync(url);
        var third = await agentClient.GetAsync(url);

        // Then
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task When_QueryLimitReached_Then_LastUseSucceedsThenConsumedAnd429()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 1));

        // When
        var first = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");
        var second = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Consumed);
        persisted.QueryCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_FullGrantIsConsumedAndEnvelopeDeleted_Then_NextDeliveryReturns429()
    {
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId, vaultId, orgId, agentId, "pk",
            GrantEnvelopeTestData.AgentVaultKey(orgId, vaultId, grantId, agentId),
            null, 1, "uses", GrantMethods.Get, Guid.NewGuid(), TestNames,
            SystemClock.Instance.GetCurrentInstant(), agentAccessEpoch: 1);
        await apiFactory.Services.SeedFullGrantAsync(full);
        var url = $"api/agent/vaults/{vaultId}/credentials/{entryId}";

        var first = await agentClient.GetAsync(url);
        var second = await agentClient.GetAsync(url);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task When_GrantTimeExpired_Then_Returns403()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var past = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromHours(1));
        await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: past, queryLimit: null));

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_NoGrantForEntry_Then_Returns404()
    {
        // Given
        var (agentClient, _, vaultId, entryId, _, _) = await SetupAsync();

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_GranularGrantForDifferentEntry_Then_Returns404()
    {
        // Given
        var (agentClient, agentId, vaultId, _, orgId, user) = await SetupAsync();
        var coveredEntry = await apiFactory.Services.SeedEntryAsync(vaultId, user.Id);
        await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, coveredEntry.Id, expiresAt: null, queryLimit: 5));
        var requestedEntry = await apiFactory.Services.SeedEntryAsync(vaultId, user.Id);

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{requestedEntry.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_FullGrantPredatesEntry_Then_CurrentEntryMaterialIsDeliveredWithoutFanOut()
    {
        // Given
        var (agentClient, agentId, vaultId, _, orgId, user) = await SetupAsync();
        await apiFactory.Services.SeedEntryAsync(vaultId, user.Id);
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId, vaultId, orgId, agentId, "pk",
            GrantEnvelopeTestData.AgentVaultKey(orgId, vaultId, grantId, agentId),
            null, 3, "uses", GrantMethods.Get, Guid.NewGuid(), TestNames, SystemClock.Instance.GetCurrentInstant(), agentAccessEpoch: 1);
        await apiFactory.Services.SeedFullGrantAsync(full);
        var newEntry = await apiFactory.Services.SeedEntryAsync(vaultId, user.Id);

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{newEntry.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("grantType").GetString().ShouldBe("full");
        result.RootElement.GetProperty("agentWrappedVaultKey").ValueKind.ShouldBe(JsonValueKind.Object);
        result.RootElement.GetProperty("entryKey").ValueKind.ShouldBe(JsonValueKind.Object);
        result.RootElement.GetProperty("memberSecret").ValueKind.ShouldBe(JsonValueKind.Object);
        result.RootElement.GetProperty("grantEnvelope").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(GrantStatus.Revoked)]
    [InlineData(GrantStatus.Denied)]
    [InlineData(GrantStatus.Pending)]
    public async Task When_GrantNotActive_Then_Returns404(GrantStatus status)
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vaultId, organizationId: orgId, agentId: agentId,
                entryId: entryId, status: status).Generate());

        // When
        var response = await agentClient.GetAsync($"api/agent/vaults/{vaultId}/credentials/{entryId}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_ConcurrentDeliveriesWithLimitOne_Then_ExactlyOneSucceeds()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 1));

        // When
        var url = $"api/agent/vaults/{vaultId}/credentials/{entryId}";
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => agentClient.GetAsync(url)));

        // Then
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode != HttpStatusCode.OK).ShouldBe(4);
    }

    [Fact]
    public async Task When_ConcurrentDeliveriesWithLimitTwo_Then_ExactlyTwoSucceedAndGrantConsumed()
    {
        // Given
        var (agentClient, agentId, vaultId, entryId, orgId, _) = await SetupAsync();
        var grant = await apiFactory.Services.SeedGranularGrantAsync(
            ActiveGranular(vaultId, orgId, agentId, entryId, expiresAt: null, queryLimit: 2));

        // When
        var url = $"api/agent/vaults/{vaultId}/credentials/{entryId}";
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ => agentClient.GetAsync(url)));

        // Then
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(2);
        responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).ShouldBe(1);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.FirstAsync(g => g.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Consumed);
        persisted.QueryCount.ShouldBe(2);
    }
}
