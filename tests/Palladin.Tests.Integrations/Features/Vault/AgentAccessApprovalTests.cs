using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
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
public sealed class AgentAccessApprovalTests(ApiFactory apiFactory) : TestBase
{
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
            apiFactory.CreateAuthenticatedClient(user), signing, agent.Id, publicKey,
            organization.Id, vault.Id, entry.Id, user.Id,
            provisioning.Request.Manifest.VaultAgentMessageKeyFingerprint);
    }

    [Fact]
    public async Task SignedEncryptedReason_IsStoredWithoutPlaintext()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);

        var (response, result) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants.Include(g => g.EncryptedReason).SingleAsync(g => g.Id == result!.GrantId);
        grant.EncryptedReason.ShouldNotBeNull();
        grant.EncryptedReason!.RequestedMethods.ShouldBe(GrantMethods.Get);
        grant.CreatedBy.ShouldBeNull();
    }

    [Fact]
    public async Task CamelCaseWireCanonicalReasonSignature_IsAccepted()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LegacyReasonSignatureDomain_IsRejected()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup) with
        {
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId, setup.VaultId, setup.EntryId, setup.AgentId, setup.Signing,
                setup.AgentMessageKeyFingerprint) with { AgentSignature = new string('A', 86) },
        };

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PendingGrant_ReturnsVerifiableEncryptedReasonFromDetailAndDashboardList()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        var response = await setup.UserClient.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{pending!.GrantId}");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reason = json.RootElement.GetProperty("encryptedReason");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        reason.GetProperty("encodedSuitePayload").GetString().ShouldBe(request.EncryptedReason.EncodedSuitePayload);
        reason.GetProperty("wrappedReasonDek").GetProperty("encodedSealedKeyPackage").GetString()
            .ShouldBe(request.EncryptedReason.WrappedReasonDek.EncodedSealedKeyPackage);
        reason.GetProperty("agentSignature").GetString().ShouldBe(request.EncryptedReason.AgentSignature);
        reason.GetProperty("descriptor").GetProperty("resourceRevision").ValueKind.ShouldBe(JsonValueKind.String);
        reason.GetProperty("descriptor").GetProperty("resourceRevision").GetString()
            .ShouldBe(request.EncryptedReason.Descriptor.ResourceRevision);
        json.RootElement.GetProperty("agentSigningPublicKey").GetString().ShouldBe(setup.Signing.PublicKeyBase64);
        json.RootElement.GetProperty("agentSigningKeyVersion").GetUInt32().ShouldBe(1u);
        json.RootElement.GetProperty("agentSigningKeyFingerprint").GetString().ShouldBe(
            WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(
                Convert.FromBase64String(setup.Signing.PublicKeyBase64), VaultKeyKind.AgentEd25519)));

        var listResponse = await setup.UserClient.GetAsync("api/dashboard/pending-grants");
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listedReason = listJson.RootElement.GetProperty("items")[0].GetProperty("encryptedReason");

        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        listedReason.GetProperty("agentSignature").GetString().ShouldBe(request.EncryptedReason.AgentSignature);
        listJson.RootElement.GetProperty("items")[0].GetProperty("agentSigningPublicKey").GetString()
            .ShouldBe(setup.Signing.PublicKeyBase64);
    }

    [Fact]
    public async Task TamperedEncryptedReason_FailsClosed()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            EncryptedReason = request.EncryptedReason with
            {
                Descriptor = request.EncryptedReason.Descriptor with
                {
                    Scope = request.EncryptedReason.Descriptor.Scope with { EntryId = Guid.NewGuid() },
                },
            },
        };

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MissingEncryptedReasonSignature_ReturnsValidationError()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            EncryptedReason = request.EncryptedReason with { AgentSignature = null! },
        };

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OversizedEncryptedReason_FailsBeforeSignatureProcessing()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        request = request with
        {
            EncryptedReason = request.EncryptedReason with
            {
                EncodedSuitePayload = new string('A', ((4_096 + 24 + 2) / 3) * 4 + 1),
            },
        };

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SignedNonCanonicalReasonRevision_ReturnsValidationError()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup) with
        {
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId,
                setup.VaultId,
                setup.EntryId,
                setup.AgentId,
                setup.Signing,
                setup.AgentMessageKeyFingerprint,
                resourceRevision: "01"),
        };

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeletingEntryRevokesPendingGrantAndDeletesEncryptedReason()
    {
        var setup = await ArrangeAsync();
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(Request(setup));
        await using (var sequenceScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = sequenceScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var vault = await writeContext.Vaults.SingleAsync(x => x.Id == setup.VaultId);
            vault.AllocateSequences(false, setup.UserId, apiFactory.FakeClock.GetCurrentInstant());
            await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        var (response, _) = await setup.UserClient.POSTAsync<
            DeleteEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(EntryEnvelopeFaker.CreateStateChangeRequest(
            setup.OrganizationId,
            setup.VaultId,
            setup.EntryId,
            1,
            EntryOperation.Deleted));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants.SingleAsync(x => x.Id == pending!.GrantId);
        grant.Status.ShouldBe(GrantStatus.Revoked);
        (await db.EncryptedReasonEnvelopes.AnyAsync(x => x.GrantRequestId == pending.GrantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task TerminalGrantRequestIdReplay_ReturnsConflictInsteadOfPersistenceFailure()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);
        await setup.UserClient.DELETEAsync<RevokeGrantEndpoint, RevokeGrantRequest>(
            new RevokeGrantRequest { VaultId = setup.VaultId, GrantId = pending!.GrantId });

        var (response, _) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approval_StoresRevisionBoundEnvelopeAndRetainsEncryptedReason()
    {
        var setup = await ArrangeAsync();
        var request = Request(setup);
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(request);
        var expiresAt = PostgreSqlInstant.Normalize(
            apiFactory.FakeClock.GetCurrentInstant() + Duration.FromHours(1));
        var envelope = GrantEnvelopeTestData.Contract(
            setup.OrganizationId, setup.VaultId, pending!.GrantId, setup.EntryId,
            setup.AgentPublicKey, expiresAt, agentId: setup.AgentId);

        var response = await setup.UserClient.PUTAsync<ApproveGrantEndpoint, ApproveGrantRequest>(
            new ApproveGrantRequest
            {
                VaultId = setup.VaultId,
                GrantId = pending.GrantId,
                GrantEntry = envelope,
                ExpiresAt = expiresAt,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await db.Grants
            .Include(g => g.EncryptedReason)
            .Include(g => g.GrantEntryScopes).ThenInclude(s => s.Envelope)
            .SingleAsync(g => g.Id == pending.GrantId);
        grant.Status.ShouldBe(GrantStatus.Active);
        grant.EncryptedReason.ShouldNotBeNull();
        grant.EncryptedReason!.GrantRequestId.ShouldBe(pending.GrantId);
        grant.GrantEntryScopes.Single().Envelope!.EntryRevision.ShouldBe(1UL);

        var detailResponse = await setup.UserClient.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{pending.GrantId}");
        var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        detailResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        detailJson.RootElement.GetProperty("encryptedReason").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task RestoredEntry_StaleActiveGrantDoesNotSuppressNewRequest()
    {
        var setup = await ArrangeAsync();
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(Request(setup));
        var approvalResponse = await setup.UserClient.PUTAsync<ApproveGrantEndpoint, ApproveGrantRequest>(
            new ApproveGrantRequest
            {
                VaultId = setup.VaultId,
                GrantId = pending!.GrantId,
                GrantEntry = GrantEnvelopeTestData.Contract(
                    setup.OrganizationId,
                    setup.VaultId,
                    pending.GrantId,
                    setup.EntryId,
                    setup.AgentPublicKey,
                    agentId: setup.AgentId),
            });
        approvalResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

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

        var rerequest = Request(setup) with
        {
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId,
                setup.VaultId,
                setup.EntryId,
                setup.AgentId,
                setup.Signing,
                setup.AgentMessageKeyFingerprint),
        };
        var (response, result) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(rerequest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Status.ShouldBe(GrantStatus.Pending);
        result.GrantId.ShouldNotBe(pending.GrantId);
    }

    [Fact]
    public async Task Approval_WithCrossScopeEnvelope_FailsClosed()
    {
        var setup = await ArrangeAsync();
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(Request(setup));
        var envelope = GrantEnvelopeTestData.Contract(
            setup.OrganizationId, setup.VaultId, pending!.GrantId, Guid.NewGuid(), setup.AgentPublicKey,
            agentId: setup.AgentId);

        var response = await setup.UserClient.PUTAsync<ApproveGrantEndpoint, ApproveGrantRequest>(
            new ApproveGrantRequest { VaultId = setup.VaultId, GrantId = pending.GrantId, GrantEntry = envelope });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProactiveApprovalInPlace_RetainsEncryptedReason()
    {
        var setup = await ArrangeAsync();
        var (_, pending) = await setup.AgentClient
            .POSTAsync<RequestAccessEndpoint, RequestAccessRequest, RequestAccessResponse>(Request(setup));
        var create = new CreateGranularGrantRequest
        {
            GrantId = pending!.GrantId,
            VaultId = setup.VaultId,
            AgentId = setup.AgentId,
            EntryId = setup.EntryId,
            Methods = GrantMethods.Get,
            GrantEntry = GrantEnvelopeTestData.Contract(
                setup.OrganizationId, setup.VaultId, pending.GrantId, setup.EntryId,
                setup.AgentPublicKey,
                agentId: setup.AgentId),
        };

        var (response, _) = await setup.UserClient
            .POSTAsync<CreateGranularGrantEndpoint, CreateGranularGrantRequest, CreateGranularGrantResponse>(create);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await db.EncryptedReasonEnvelopes.AnyAsync(x => x.GrantRequestId == pending.GrantId)).ShouldBeTrue();
    }

    private static RequestAccessRequest Request(Setup setup, GrantMethods methods = GrantMethods.Get) =>
        new()
        {
            VaultId = setup.VaultId,
            EntryId = setup.EntryId,
            RequestedMethods = methods,
            EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                setup.OrganizationId, setup.VaultId, setup.EntryId, setup.AgentId, setup.Signing,
                setup.AgentMessageKeyFingerprint, methods),
        };

    private sealed record Setup(
        HttpClient AgentClient,
        HttpClient UserClient,
        AgentRequestSigning Signing,
        Guid AgentId,
        string AgentPublicKey,
        Guid OrganizationId,
        Guid VaultId,
        Guid EntryId,
        Guid UserId,
        string AgentMessageKeyFingerprint);
}
