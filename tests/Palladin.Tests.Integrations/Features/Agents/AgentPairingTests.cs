using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FastEndpoints;
using FastEndpoints.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSec.Cryptography;
using NSubstitute;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Pairing;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class AgentPairingTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(Permission.AgentManage)]
    [InlineData(Permission.AgentManage | Permission.ReadApiKey)]
    [InlineData(Permission.WriteApiKey)]
    public async Task When_CreateOnlyPairingPermissionsAreIncomplete_Then_BothEndpointsAreForbidden(Permission permissions)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, permissions);
        var pairingId = Guid.NewGuid();

        // When
        var claim = await client.POSTAsync<ClaimAgentPairingForNewKeyEndpoint, ClaimAgentPairingRequest>(
            new ClaimAgentPairingRequest { PairingId = pairingId });
        var approve = await client.POSTAsync<ApproveAgentPairingWithNewKeyEndpoint, ApproveAgentPairingWithNewKeyRequest>(
            new ApproveAgentPairingWithNewKeyRequest { PairingId = pairingId, DisplayName = "Friendly Fox", NewApiKeyName = "Automation" });

        // Then
        claim.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        approve.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_PersistedAgentNameHasProviderSpecificUnicodeCasing_Then_IdenticalNameIsUnavailable()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>();
        var coordinator = scope.ServiceProvider.GetRequiredService<AgentDisplayNameCoordinator>();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var existing = Agent.Create(
            Guid.NewGuid(),
            organization.Id,
            AgentFaker.GeneratePublicKey(),
            AgentFaker.GeneratePublicKey(),
            "custom/runtime",
            now,
            "ı");
        existing.Activate(user.Id, now, "ı", "custom/runtime", null, null);
        writeContext.Add(existing);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);

        // When
        var available = await coordinator.IsAvailableAsync(
            organization.Id,
            "ı",
            now,
            null,
            null,
            TestContext.Current.CancellationToken);

        // Then
        available.ShouldBeFalse();
    }

    [Fact]
    public async Task When_UserApprovesExistingLogicalApiKey_Then_CreatesStandardActiveAgentWithHiddenCredential()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id, "Pairing Approver");
        var (logicalApiKey, _) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            createdBy: user.Id);
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var publicKey = ExportPublicKey(encryptionKey);
        var pairingClient = CreatePairingClient(pairingId, signing);
        var startRequest = new StartAgentPairingRequest
        {
            PairingId = pairingId,
            PublicKey = publicKey,
            SigningPublicKey = signing.PublicKeyBase64,
            DisplayName = "  Cafe\u0301 Helper  ",
            Type = "  custom-runtime  ",
        };

        // When — runtime starts an anonymous, signed request with untrusted metadata.
        var (startResponse, start) = await pairingClient
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
                startRequest);

        // Then — the URL is an opaque handle and does not carry metadata or key material.
        startResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        start.ShouldNotBeNull();
        start.ApprovalUrl.ShouldBe($"http://127.0.0.1:5173/agent-pairing/{pairingId:D}");
        start.ApprovalUrl.ShouldNotContain("Café");
        start.ApprovalUrl.ShouldNotContain("custom-runtime");
        start.ApprovalUrl.ShouldNotContain(publicKey);

        // And — an authenticated organization claims, reviews and reserves the final name.
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            Permission.AgentManage | Permission.ReadApiKey);
        var (claimResponse, claim) = await userClient
            .POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
                new ClaimAgentPairingRequest { PairingId = pairingId });
        claimResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        claim.DisplayName.ShouldBe("Café Helper");
        claim.Type.ShouldBe("custom-runtime");
        claim.ApiKeys.Select(x => x.ApiKeyId).ShouldContain(logicalApiKey.Id);
        claim.ApiKeys.ShouldAllBe(x => x.KeyHint.StartsWith("pl_••••", StringComparison.Ordinal));

        var reserveResponse = await userClient
            .POSTAsync<ReserveAgentPairingDisplayNameEndpoint, ReserveAgentPairingDisplayNameRequest>(
                new ReserveAgentPairingDisplayNameRequest
                {
                    PairingId = pairingId,
                    DisplayName = "  Bursztynowy Lis  ",
                });
        reserveResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (approveResponse, approved) = await userClient
            .POSTAsync<ApproveAgentPairingEndpoint, ApproveAgentPairingRequest, ApproveAgentPairingResponse>(
                new ApproveAgentPairingRequest
                {
                    PairingId = pairingId,
                    DisplayName = "Bursztynowy Lis",
                    ApiKeyId = logicalApiKey.Id,
                    IconKey = "smart_toy",
                });
        approveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A runtime that crashed after approval can replay the immutable start request.
        // The user-edited final name must not turn that recovery into a conflict.
        var (terminalRetryResponse, terminalRetry) = await pairingClient
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
                startRequest);
        terminalRetryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        terminalRetry.ShouldBe(start);

        // And — the Agent is a normal active Agent on the standard list.
        var (listResponse, agents) = await userClient.GETAsync<ListAgentsEndpoint, ListAgentsResponse>();
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pairedAgent = agents.Items.Single(x => x.AgentId == approved.AgentId);
        pairedAgent.Status.ShouldBe(AgentStatus.Active);
        pairedAgent.Name.ShouldBe("Bursztynowy Lis");
        pairedAgent.IconKey.ShouldBe("smart_toy");
        pairedAgent.Type.ShouldBe("custom-runtime");

        // And — only the logical API key is user-visible; its technical child is internal.
        var (_, listedApiKeys) = await userClient.GETAsync<ListApiKeysEndpoint, ListApiKeysResponse>();
        listedApiKeys.Items.Select(x => x.ApiKeyId).ShouldContain(logicalApiKey.Id);
        listedApiKeys.Items.Count.ShouldBe(1);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            var credential = await readContext.ApiKeyCredentials.SingleAsync(
                x => x.AgentId == approved.AgentId,
                TestContext.Current.CancellationToken);
            credential.ApiKeyId.ShouldBe(logicalApiKey.Id);
        }

        // And — the runtime can decrypt the one-time envelope and use the standard Agent API.
        // Approval is terminal: advancing beyond the request lifetime must not hide
        // the encrypted credential before the authorized runtime can consume it.
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        HttpResponseMessage statusResponse;
        try
        {
            apiFactory.FakeClock.AdvanceMinutes(5);
            statusResponse = await GetPairingStatusAtServerTimeAsync(pairingId, signing);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
        statusResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var activeStatus = await statusResponse.Content.ReadFromJsonAsync<GetAgentPairingStatusResponse>(
            TestContext.Current.CancellationToken);
        activeStatus.ShouldNotBeNull();
        activeStatus.Status.ShouldBe("active");
        activeStatus.DisplayName.ShouldBe("Bursztynowy Lis");
        activeStatus.Type.ShouldBe("custom-runtime");
        activeStatus.Credential.ShouldNotBeNull();

        var technicalPlaintext = DecryptCredential(
            pairingId,
            activeStatus.OrganizationId!.Value,
            activeStatus.AgentId!.Value,
            activeStatus.ApiKeyId!.Value,
            publicKey,
            encryptionKey,
            activeStatus.Credential);
        Should.Throw<CryptographicException>(() => DecryptCredential(
            pairingId,
            Guid.NewGuid(),
            activeStatus.AgentId.Value,
            activeStatus.ApiKeyId.Value,
            publicKey,
            encryptionKey,
            activeStatus.Credential));
        try
        {
            var agentClient = apiFactory.CreateSignedAgentClient(
                approved.AgentId,
                technicalPlaintext,
                publicKey,
                signing);
            var (profileResponse, profile) = await agentClient
                .GETAsync<GetAgentMeEndpoint, GetAgentMeResponse>();
            profileResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            profile.AgentId.ShouldBe(approved.AgentId);
            profile.OrganizationId.ShouldBe(organization.Id);
            profile.Status.ShouldBe(AgentStatus.Active);

            // A technical credential is bound to exactly one Agent identity.
            using var foreignEncryptionKey = CreateEncryptionKey();
            var foreignKey = ExportPublicKey(foreignEncryptionKey);
            var foreignClient = apiFactory.CreateSignedAgentClient(
                approved.AgentId,
                technicalPlaintext,
                foreignKey,
                signing);
            var foreignResponse = await foreignClient.GetAsync(
                "api/agent/me",
                TestContext.Current.CancellationToken);
            foreignResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            // Hidden children are not authentication-cached, so parent revoke is authoritative immediately.
            var revokeClient = apiFactory.CreateAuthenticatedClient(user, Permission.WriteApiKey);
            var revokeResponse = await revokeClient.DeleteAsync(
                $"api/api-keys/{logicalApiKey.Id}",
                TestContext.Current.CancellationToken);
            revokeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            var revokedChildResponse = await agentClient.GetAsync(
                "api/agent/me",
                TestContext.Current.CancellationToken);
            revokedChildResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            // This plaintext exists only in the test process to prove envelope compatibility.
            technicalPlaintext = string.Empty;
        }

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
            var persistedAgent = await readContext.Agents.SingleAsync(
                x => x.Id == approved.AgentId,
                TestContext.Current.CancellationToken);
            persistedAgent.LastUsedApiKeyId.ShouldBe(logicalApiKey.Id);
        }
    }

    [Theory]
    [InlineData(Permission.AgentManage | Permission.WriteApiKey)]
    [InlineData(Permission.AgentManage | Permission.WriteApiKey | Permission.ReadApiKey)]
    public async Task When_UserChoosesCreateApiKey_Then_CreatesLogicalKeyAndKeepsTechnicalCredentialHidden(Permission permissions)
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var (existingKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id, createdBy: user.Id);
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        var request = new StartAgentPairingRequest
        {
            PairingId = pairingId,
            PublicKey = ExportPublicKey(encryptionKey),
            SigningPublicKey = signing.PublicKeyBase64,
        };
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(request);
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            permissions);
        var (_, claim) = await userClient
            .POSTAsync<ClaimAgentPairingForNewKeyEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
                new ClaimAgentPairingRequest { PairingId = pairingId });
        if ((permissions & Permission.ReadApiKey) == 0)
        {
            var deniedClaim = await userClient.POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest>(
                new ClaimAgentPairingRequest { PairingId = pairingId });
            deniedClaim.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var deniedApprove = await userClient.POSTAsync<ApproveAgentPairingEndpoint, ApproveAgentPairingRequest>(
                new ApproveAgentPairingRequest { PairingId = pairingId, DisplayName = "No selection", ApiKeyId = existingKey.Id });
            deniedApprove.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        claim.DisplayName.ShouldBeNull();
        claim.Type.ShouldBeNull();
        claim.ApiKeys.ShouldBeEmpty();
        claim.CanCreateApiKey.ShouldBeTrue();
        await userClient.POSTAsync<ReserveAgentPairingDisplayNameEndpoint, ReserveAgentPairingDisplayNameRequest>(
            new ReserveAgentPairingDisplayNameRequest { PairingId = pairingId, DisplayName = "Spokojna Wydra" });

        // When
        var (response, approved) = await userClient
            .POSTAsync<ApproveAgentPairingWithNewKeyEndpoint, ApproveAgentPairingWithNewKeyRequest, ApproveAgentPairingResponse>(
                new ApproveAgentPairingWithNewKeyRequest
                {
                    PairingId = pairingId,
                    DisplayName = "Spokojna Wydra",
                    NewApiKeyName = "Local Codex",
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var agent = await readContext.Agents.SingleAsync(
            x => x.Id == approved.AgentId,
            TestContext.Current.CancellationToken);
        var apiKey = await readContext.ApiKeys.SingleAsync(
            x => x.Id == agent.LastUsedApiKeyId,
            TestContext.Current.CancellationToken);
        var technicalCredential = await readContext.ApiKeyCredentials.SingleAsync(
            x => x.ApiKeyId == apiKey.Id,
            TestContext.Current.CancellationToken);
        apiKey.Name.ShouldBe("Local Codex");
        apiKey.OrganizationId.ShouldBe(organization.Id);
        apiKey.Id.ShouldNotBe(existingKey.Id);
        agent.Status.ShouldBe(AgentStatus.Active);
        technicalCredential.KeyHash.ShouldNotBe(apiKey.KeyHash);
        (await readContext.ApiKeyCredentials.CountAsync(
            x => x.ApiKeyId == apiKey.Id,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_FinalNameWasNotReserved_Then_ApprovalIsRejectedWithoutCreatingAgent()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
            });
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            Permission.AgentManage | Permission.ReadApiKey);
        await userClient.POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
            new ClaimAgentPairingRequest { PairingId = pairingId });

        // When
        var (response, _) = await userClient
            .POSTAsync<ApproveAgentPairingEndpoint, ApproveAgentPairingRequest, ApproveAgentPairingResponse>(
                new ApproveAgentPairingRequest
                {
                    PairingId = pairingId,
                    DisplayName = "Unreserved Name",
                    ApiKeyId = apiKey.Id,
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.Agents.CountAsync(
            x => x.OrganizationId == organization.Id,
            TestContext.Current.CancellationToken)).ShouldBe(0);
        (await readContext.ApiKeyCredentials.CountAsync(
            x => x.ApiKeyId == apiKey.Id,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task When_PairingExpires_Then_StatusIsValueFreeAndCannotBeClaimed()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        await apiFactory.Services.SeedUserAsync();
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
                DisplayName = "Private Helper",
                Type = "custom-runtime",
            });
        try
        {
            apiFactory.FakeClock.AdvanceMinutes(29);
            var pendingResponse = await GetPairingStatusAtServerTimeAsync(pairingId, signing);
            var pending = await pendingResponse.Content.ReadFromJsonAsync<GetAgentPairingStatusResponse>(TestContext.Current.CancellationToken);
            pending.ShouldNotBeNull();
            pending.Status.ShouldBe("pending");
            apiFactory.FakeClock.AdvanceMinutes(2);

            // When
            var statusResponse = await GetPairingStatusAtServerTimeAsync(pairingId, signing);
            var status = await statusResponse.Content.ReadFromJsonAsync<GetAgentPairingStatusResponse>(
                TestContext.Current.CancellationToken);
            status.ShouldNotBeNull();
            // Then
            statusResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            status.Status.ShouldBe("expired");
            status.OrganizationId.ShouldBeNull();
            status.AgentId.ShouldBeNull();
            status.ApiKeyId.ShouldBeNull();
            status.Credential.ShouldBeNull();
            status.DisplayName.ShouldBeNull();
            status.Type.ShouldBeNull();
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_RuntimeRetriesSameSignedPayload_Then_StartIsIdempotentButChangedMetadataConflicts()
    {
        // Given
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var client = CreatePairingClient(pairingId, signing);
        var request = new StartAgentPairingRequest
        {
            PairingId = pairingId,
            PublicKey = ExportPublicKey(encryptionKey),
            SigningPublicKey = signing.PublicKeyBase64,
            Type = "custom/runtime",
        };

        // When
        var (firstResponse, first) = await client
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(request);
        var (retryResponse, retry) = await client
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(request);
        var (conflictResponse, _) = await client
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
                request with { Type = "changed" });

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.ShouldBe(first);
        conflictResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_StartUsesRouteEquivalentTrailingSlash_Then_ExactBodyIsStillAuthenticated()
    {
        // Given
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var client = CreatePairingClient(pairingId, signing);

        // When
        var response = await client.PostAsJsonAsync(
            "/api/agent-pairings/",
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
            },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.AgentPairingRequests.CountAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_StartUsesNonCanonicalPublicKey_Then_RequestIsRejected()
    {
        // Given
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var client = CreatePairingClient(pairingId, signing);

        // When
        var (response, _) = await client
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
                new StartAgentPairingRequest
                {
                    PairingId = pairingId,
                    PublicKey = $"{ExportPublicKey(encryptionKey)} ",
                    SigningPublicKey = signing.PublicKeyBase64,
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.AgentPairingRequests.CountAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task When_StartSignatureDoesNotMatchDeclaredIdentity_Then_RequestIsNotCreated()
    {
        // Given
        var pairingId = Guid.NewGuid();
        var declaredSigning = AgentRequestSigning.Generate();
        var attackerSigning = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var client = CreatePairingClient(pairingId, attackerSigning);

        // When
        var (response, _) = await client
            .POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
                new StartAgentPairingRequest
                {
                    PairingId = pairingId,
                    PublicKey = ExportPublicKey(encryptionKey),
                    SigningPublicKey = declaredSigning.PublicKeyBase64,
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.AgentPairingRequests.CountAsync(
            x => x.Id == pairingId,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task When_AnotherOrganizationTriesToClaim_Then_PairingRemainsBoundToFirstOrganization()
    {
        // Given
        var (firstUser, _, _) = await apiFactory.Services.SeedUserAsync();
        var (secondUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(firstUser.Id);
        await apiFactory.Services.SeedAgentsUserAsync(secondUser.Id);
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var client = CreatePairingClient(pairingId, signing);
        await client.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
            });
        var permissions = Permission.AgentManage | Permission.ReadApiKey;

        // When
        var (firstResponse, _) = await apiFactory.CreateAuthenticatedClient(firstUser, permissions)
            .POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
                new ClaimAgentPairingRequest { PairingId = pairingId });
        var (secondResponse, _) = await apiFactory.CreateAuthenticatedClient(secondUser, permissions)
            .POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
                new ClaimAgentPairingRequest { PairingId = pairingId });

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_StandardAgentUpdateTargetsReservedPairingName_Then_NameRemainsReserved()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Name, "Existing Agent"));
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
            });
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            Permission.AgentManage | Permission.ReadApiKey);
        await userClient.POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
            new ClaimAgentPairingRequest { PairingId = pairingId });
        await userClient.POSTAsync<ReserveAgentPairingDisplayNameEndpoint, ReserveAgentPairingDisplayNameRequest>(
            new ReserveAgentPairingDisplayNameRequest
            {
                PairingId = pairingId,
                DisplayName = "Quiet Otter",
            });

        // When
        var response = await userClient.PATCHAsync<UpdateAgentEndpoint, UpdateAgentRequest>(
            new UpdateAgentRequest { AgentId = agent.Id, Name = "quiet otter" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.Agents.SingleAsync(
            x => x.Id == agent.Id,
            TestContext.Current.CancellationToken)).Name.ShouldBe("Existing Agent");
    }

    [Fact]
    public async Task When_NameBecomesUnavailableAfterReservation_Then_ApprovalCreatesNothing()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
            });
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            Permission.AgentManage | Permission.ReadApiKey);
        await userClient.POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
            new ClaimAgentPairingRequest { PairingId = pairingId });
        await userClient.POSTAsync<ReserveAgentPairingDisplayNameEndpoint, ReserveAgentPairingDisplayNameRequest>(
            new ReserveAgentPairingDisplayNameRequest
            {
                PairingId = pairingId,
                DisplayName = "Calm Badger",
            });
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Name, "calm badger"));

        // When
        var (response, _) = await userClient
            .POSTAsync<ApproveAgentPairingEndpoint, ApproveAgentPairingRequest, ApproveAgentPairingResponse>(
                new ApproveAgentPairingRequest
                {
                    PairingId = pairingId,
                    DisplayName = "Calm Badger",
                    ApiKeyId = apiKey.Id,
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        (await readContext.ApiKeyCredentials.CountAsync(
            x => x.ApiKeyId == apiKey.Id,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task When_NameCollidesOrPairingIsRejected_Then_NoMetadataLeaksThroughStatus()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id).RuleFor(x => x.Name, "Amber Fox"));
        var pairingId = Guid.NewGuid();
        var signing = AgentRequestSigning.Generate();
        using var encryptionKey = CreateEncryptionKey();
        var pairingClient = CreatePairingClient(pairingId, signing);
        await pairingClient.POSTAsync<StartAgentPairingEndpoint, StartAgentPairingRequest, StartAgentPairingResponse>(
            new StartAgentPairingRequest
            {
                PairingId = pairingId,
                PublicKey = ExportPublicKey(encryptionKey),
                SigningPublicKey = signing.PublicKeyBase64,
                DisplayName = "Private Runtime",
                Type = "custom/runtime",
            });
        var userClient = apiFactory.CreateAuthenticatedClient(
            user,
            Permission.AgentManage | Permission.ReadApiKey);
        await userClient.POSTAsync<ClaimAgentPairingEndpoint, ClaimAgentPairingRequest, ClaimAgentPairingResponse>(
            new ClaimAgentPairingRequest { PairingId = pairingId });

        // When
        var collision = await userClient
            .POSTAsync<ReserveAgentPairingDisplayNameEndpoint, ReserveAgentPairingDisplayNameRequest>(
                new ReserveAgentPairingDisplayNameRequest { PairingId = pairingId, DisplayName = "amber fox" });
        var rejected = await userClient.POSTAsync<RejectAgentPairingEndpoint, RejectAgentPairingRequest>(
            new RejectAgentPairingRequest { PairingId = pairingId });
        var statusResponse = await pairingClient.GetAsync(
            $"api/agent-pairings/{pairingId:D}/status",
            TestContext.Current.CancellationToken);
        var status = await statusResponse.Content.ReadFromJsonAsync<GetAgentPairingStatusResponse>(
            TestContext.Current.CancellationToken);

        // Then
        collision.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        rejected.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        statusResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        status.ShouldNotBeNull();
        status.Status.ShouldBe("rejected");
        status.DisplayName.ShouldBeNull();
        status.Type.ShouldBeNull();
        status.Credential.ShouldBeNull();
    }

    private HttpClient CreatePairingClient(Guid pairingId, AgentRequestSigning signing) =>
        new(signing.CreateSigningHandler(pairingId, apiFactory.CreateHandler()))
        {
            BaseAddress = apiFactory.Client.BaseAddress,
        };

    private async Task<HttpResponseMessage> GetPairingStatusAtServerTimeAsync(
        Guid pairingId,
        AgentRequestSigning signing)
    {
        var path = $"/api/agent-pairings/{pairingId:D}/status";
        var timestamp = apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var bodyHash = Convert.ToBase64String(SHA256.HashData([]));
        var signature = signing.SignCanonical(string.Join('\n', "GET", path, timestamp, nonce, bodyHash));
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AgentAuthenticationOptions.AgentIdHeader, pairingId.ToString());
        request.Headers.Add(AgentAuthenticationOptions.AgentTimestampHeader, timestamp);
        request.Headers.Add(AgentAuthenticationOptions.AgentNonceHeader, nonce);
        request.Headers.Add(AgentAuthenticationOptions.AgentSignatureHeader, signature);
        return await apiFactory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Key CreateEncryptionKey() =>
        Key.Create(
            KeyAgreementAlgorithm.X25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static string ExportPublicKey(Key key) =>
        Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static string DecryptCredential(
        Guid pairingId,
        Guid organizationId,
        Guid agentId,
        Guid apiKeyId,
        string recipientPublicKey,
        Key recipientKey,
        AgentPairingCredentialEnvelopeResponse envelope)
    {
        envelope.Suite.ShouldBe(AgentPairingCredentialProtector.Suite);
        var ephemeralPublicKey = PublicKey.Import(
            KeyAgreementAlgorithm.X25519,
            Convert.FromBase64String(envelope.EphemeralPublicKey),
            KeyBlobFormat.RawPublicKey);
        using var shared = KeyAgreementAlgorithm.X25519.Agree(
            recipientKey,
            ephemeralPublicKey,
            new SharedSecretCreationParameters());
        shared.ShouldNotBeNull();
        using var key = KeyDerivationAlgorithm.HkdfSha256.DeriveKey(
            shared,
            Encoding.UTF8.GetBytes(pairingId.ToString("D")),
            "palladin/agent-pairing/v1/credential"u8.ToArray(),
            AeadAlgorithm.XChaCha20Poly1305,
            new KeyCreationParameters());
        var plaintext = AeadAlgorithm.XChaCha20Poly1305.Decrypt(
            key,
            Convert.FromBase64String(envelope.Nonce),
            AgentPairingCredentialProtector.BuildAssociatedData(
                pairingId, organizationId, agentId, apiKeyId, recipientPublicKey),
            Convert.FromBase64String(envelope.Ciphertext))
            ?? throw new CryptographicException("Pairing credential authentication failed.");
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
