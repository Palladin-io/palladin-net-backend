using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class GetOrRequestCredentialTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task NoGrant_WithSignedReason_CreatesPendingWithoutPlaintext()
    {
        var setup = await ArrangeAsync();
        var request = NewRequest(setup);

        var (response, result) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        result!.Access.ShouldBe("pending");
        result.Created.ShouldBe(true);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants.Include(g => g.EncryptedReason).SingleAsync(g => g.Id == result.GrantId);
        grant.EncryptedReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExistingPending_DoesNotCreateDuplicateOrAcceptReplacementReason()
    {
        var setup = await ArrangeAsync();
        var firstRequest = NewRequest(setup);
        var (_, first) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(firstRequest);
        var replacement = NewRequest(setup);

        var (response, second) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(replacement);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second!.GrantId.ShouldBe(first!.GrantId);
        second.Created.ShouldBeNull();
    }

    [Fact]
    public async Task CrossAgentReason_FailsClosed()
    {
        var setup = await ArrangeAsync();
        var request = NewRequest(setup) with
        {
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId, setup.VaultId, setup.EntryId, Guid.NewGuid(), setup.Signing,
                setup.AgentMessageKeyFingerprint),
        };

        var (response, _) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TerminalGrantRequestIdReplay_ReturnsConflictInsteadOfPersistenceFailure()
    {
        var setup = await ArrangeAsync();
        var request = NewRequest(setup);
        var (_, first) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(request);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var grant = await db.Grants.Include(g => g.EncryptedReason)
                .SingleAsync(g => g.Id == first!.GrantId);
            grant.RevokeBySystem(
                new GrantNames("agent", "entry", "vault", GrantNames.SystemActor),
                apiFactory.FakeClock.GetCurrentInstant());
            await db.CommitAsync(TestContext.Current.CancellationToken);
        }

        var (response, _) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NewerConsumedFullScope_AllowsRerequestDespiteOlderDenial()
    {
        var setup = await ArrangeAsync();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var denied = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Denied,
            createdAt: now.Minus(Duration.FromMinutes(1))).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(denied);
        var consumed = GrantFaker.CreateFull(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            status: GrantStatus.Consumed,
            createdAt: now).Generate();
        consumed.DeleteAgentEnvelopes();
        await apiFactory.Services.SeedFullGrantAsync(consumed);

        var (response, result) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(
                NewRequest(setup));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        result!.Access.ShouldBe("pending");
        result.Created.ShouldBe(true);
    }

    [Fact]
    public async Task RestoredEntry_StaleActiveGrantCreatesFreshPendingRequest()
    {
        var setup = await ArrangeAsync();
        var denied = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId,
            status: GrantStatus.Denied,
            createdAt: apiFactory.FakeClock.GetCurrentInstant().Minus(Duration.FromMinutes(1))).Generate();
        await apiFactory.Services.SeedGranularGrantAsync(denied);
        var active = GrantFaker.CreateGranular(
            vaultId: setup.VaultId,
            organizationId: setup.OrganizationId,
            agentId: setup.AgentId,
            entryId: setup.EntryId).Generate();
        active.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            setup.OrganizationId, setup.VaultId, active.Id, setup.EntryId, active.Methods));
        await apiFactory.Services.SeedGranularGrantAsync(active);

        await using (var sequenceScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = sequenceScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.SingleAsync(x => x.Id == setup.VaultId);
            vault.AllocateSequences(false, setup.UserId, apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        var archive = EntryEnvelopeFaker.CreateStateChangeRequest(
            setup.OrganizationId, setup.VaultId, setup.EntryId, 1, EntryOperation.Archived);
        var (archiveResponse, _) = await setup.UserClient.POSTAsync<
            ArchiveEntryEndpoint, ChangeEntryStateRequest, ChangeEntryStateResponse>(archive);
        archiveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var restore = EntryEnvelopeFaker.CreateStateChangeRequest(
            setup.OrganizationId,
            setup.VaultId,
            setup.EntryId,
            2,
            EntryOperation.Restored);
        var (restoreResponse, _) = await setup.UserClient.POSTAsync<
            RestoreEntryEndpoint, ChangeEntryStateRequest, ChangeEntryStateResponse>(restore);
        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var request = NewRequest(setup) with
        {
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId,
                setup.VaultId,
                setup.EntryId,
                setup.AgentId,
                setup.Signing,
                setup.AgentMessageKeyFingerprint),
        };
        var (response, result) = await setup.Client
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        result!.Access.ShouldBe("pending");
        result.Created.ShouldBe(true);
        result.GrantId.ShouldNotBe(active.Id);
    }

    [Fact]
    public async Task InjectOnlyGrant_WithLeastPrivilegeMethods_ReturnsPolicySpecificDenial()
    {
        var setup = await ArrangeAsync();
        var grantId = Guid.NewGuid();
        var methods = GrantMethods.Inject;
        var scope = GrantEnvelopeTestData.Scope(
            setup.OrganizationId,
            setup.VaultId,
            grantId,
            setup.EntryId,
            methods: methods,
            remainingUses: 5,
            fieldIds: ["cardNumber", "expiryMonth", "expiryYear"],
            deliveryPolicy: GrantDeliveryPolicy.InjectOnly);
        var grant = GranularGrant.CreateProactively(
            grantId,
            setup.VaultId,
            setup.OrganizationId,
            setup.AgentId,
            "pk",
            setup.EntryId,
            scope,
            expiresAt: null,
            queryLimit: 5,
            expirySource: "uses",
            methods,
            createdBy: Guid.NewGuid(),
            new GrantNames("agent", "entry", "vault", "actor"),
            apiFactory.FakeClock.GetCurrentInstant(),
            agentAccessEpoch: 1);
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        var (response, result) = await setup.Client.POSTAsync<
            GetOrRequestCredentialEndpoint,
            GetOrRequestCredentialRequest,
            GetOrRequestCredentialResponse>(NewRequest(setup));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        result!.Access.ShouldBe("credit-card-inject-only");
        await using var scopeForAssertion = apiFactory.Services.CreateAsyncScope();
        var db = scopeForAssertion.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.Grants.SingleAsync(x => x.Id == grantId)).QueryCount.ShouldBe(0);
    }

    private async Task<Setup> ArrangeAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var publicKey = provisioning.X25519PublicKey;
        var signing = provisioning.RequestSigning;
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(id: agentId, organizationId: organization.Id, publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id, status: AgentStatus.Active, id: agent.Id, publicKey: publicKey,
            signingPublicKey: signing.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(provisioning.Request, user.Id);
        return new Setup(
            apiFactory.CreateSignedAgentClient(agent.Id, plaintext, publicKey, signing),
            apiFactory.CreateAuthenticatedClient(user),
            signing,
            agent.Id, organization.Id, vault.Id, entry.Id, user.Id,
            provisioning.Request.Manifest.VaultAgentMessageKeyFingerprint);
    }

    private static GetOrRequestCredentialRequest NewRequest(Setup setup) => new()
    {
        VaultId = setup.VaultId,
        EntryId = setup.EntryId,
        Method = GrantMethods.Get,
        RequestedMethods = GrantMethods.Get,
        EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
            setup.OrganizationId, setup.VaultId, setup.EntryId, setup.AgentId, setup.Signing,
            setup.AgentMessageKeyFingerprint),
    };

    private sealed record Setup(
        HttpClient Client,
        HttpClient UserClient,
        AgentRequestSigning Signing,
        Guid AgentId,
        Guid OrganizationId,
        Guid VaultId,
        Guid EntryId,
        Guid UserId,
        string AgentMessageKeyFingerprint);
}
