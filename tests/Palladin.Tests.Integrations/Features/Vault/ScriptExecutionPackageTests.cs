using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSec.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class ScriptExecutionPackageTests(ApiFactory apiFactory) : TestBase
{
    private async Task<Setup> SetupScriptAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var script = await apiFactory.Services.SeedEntryAsync(
            vault.Id,
            user.Id,
            EntryFaker.Create(organizationId: organization.Id, vaultId: vault.Id, createdBy: user.Id)
                .RuleFor(entry => entry.DeliveryPolicy, GrantDeliveryPolicy.ExecOnly));
        var reference = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var seededVault = await seedContext.Vaults.SingleAsync(value => value.Id == vault.Id);
            while (seededVault.MemberSequence.Value < 2)
            {
                seededVault.AllocateSequences(false, user.Id, SystemClock.Instance.GetCurrentInstant());
            }
            await seedContext.SaveChangesAsync();
        }
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(
            organization.Id, vault.Id, agentId);
        var publicKey = provisioning.X25519PublicKey;
        var signing = provisioning.RequestSigning;
        var identityAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    id: agentId,
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(agent => agent.Status, AgentStatus.Active)
                .RuleFor(agent => agent.AccessEpoch, (_, _) => 1u));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id,
            status: AgentStatus.Active,
            id: identityAgent.Id,
            publicKey: publicKey,
            signingPublicKey: signing.PublicKeyBase64);
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(
            provisioning.Request, user.Id);
        return new Setup(
            apiFactory.CreateSignedAgentClient(identityAgent.Id, apiKey, publicKey, signing),
            apiFactory.CreateAuthenticatedClient(user),
            organization.Id,
            vault.Id,
            script.Id,
            reference.Id,
            identityAgent.Id,
            publicKey,
            signing,
            provisioning.Request.Manifest.VaultAgentMessageKeyFingerprint);
    }

    [Fact]
    public async Task ExecRequest_CreatesOnePendingScriptExecutionGrant()
    {
        var setup = await SetupScriptAsync();
        var encryptedReason = GrantEnvelopeTestData.EncryptedReason(
            setup.OrganizationId,
            setup.VaultId,
            setup.ScriptEntryId,
            setup.AgentId,
            setup.Signing,
            setup.AgentMessageKeyFingerprint,
            GrantMethods.Exec);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/request-access",
            new { setup.VaultId, setup.ScriptEntryId, EncryptedReason = encryptedReason },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        var grantId = body.RootElement.GetProperty("grantId").GetGuid();
        body.RootElement.GetProperty("status").GetString().ShouldBe("pending");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grants = await readContext.Grants.OfType<ScriptExecutionGrant>()
            .Include(grant => grant.EncryptedReason)
            .Include(grant => grant.ScriptExecutionPackage)
            .Include(grant => grant.ScriptExecutionScopes)
            .Where(grant => grant.AgentId == setup.AgentId
                            && grant.VaultId == setup.VaultId
                            && grant.ScriptEntryId == setup.ScriptEntryId)
            .ToListAsync();
        grants.Count.ShouldBe(1);
        var pending = grants.Single();
        pending.Id.ShouldBe(grantId);
        pending.Status.ShouldBe(GrantStatus.Pending);
        pending.Methods.ShouldBe(GrantMethods.Exec);
        pending.EncryptedReason.ShouldNotBeNull();
        pending.ScriptExecutionPackage.ShouldBeNull();
        pending.ScriptExecutionScopes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PendingScriptExecutionGrant_ApprovesOneCompletePackage()
    {
        var setup = await SetupScriptAsync();
        var encryptedReason = GrantEnvelopeTestData.EncryptedReason(
            setup.OrganizationId,
            setup.VaultId,
            setup.ScriptEntryId,
            setup.AgentId,
            setup.Signing,
            setup.AgentMessageKeyFingerprint,
            GrantMethods.Exec);
        var request = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/request-access",
            new { setup.VaultId, setup.ScriptEntryId, EncryptedReason = encryptedReason },
            TestContext.Current.CancellationToken);
        using var requestBody = JsonDocument.Parse(await request.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        var grantId = requestBody.RootElement.GetProperty("grantId").GetGuid();
        var package = PackageContract(setup, grantId);

        var approval = await setup.UserClient.PutAsJsonAsync(
            $"api/vaults/{setup.VaultId}/grants/{grantId}/approve",
            new
            {
                setup.VaultId,
                GrantId = grantId,
                ScriptPackage = package,
                QueryLimit = 5,
                Methods = GrantMethods.Exec,
            },
            TestContext.Current.CancellationToken);

        approval.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approval.Content.ReadAsStringAsync());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grant = await readContext.Grants.OfType<ScriptExecutionGrant>()
            .Include(value => value.EncryptedReason)
            .Include(value => value.ScriptExecutionPackage)
            .Include(value => value.ScriptExecutionScopes)
            .SingleAsync(value => value.Id == grantId);
        grant.Status.ShouldBe(GrantStatus.Active);
        grant.Methods.ShouldBe(GrantMethods.Exec);
        grant.QueryLimit.ShouldBe(5);
        grant.EncryptedReason.ShouldNotBeNull();
        grant.ScriptExecutionPackage.ShouldNotBeNull();
        grant.ScriptExecutionScopes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DirectGrant_DeliversOneOpaquePackageAndCountsOneUse()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("authorizationSource").GetString().ShouldBe("scriptExecution");
        body.RootElement.GetProperty("queryCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("scriptPackage").GetProperty("scopes").GetArrayLength().ShouldBe(2);
        body.RootElement.GetProperty("agentWrappedVaultKey").ValueKind.ShouldBe(JsonValueKind.Null);
        body.RootElement.GetProperty("vaultEntries").ValueKind.ShouldBe(JsonValueKind.Null);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(value => value.Id == grant.Id);
        persisted.QueryCount.ShouldBe(1);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task ExactScriptRequest_UsesCurrentRevisionWithoutDiscoveryRoundTrip()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("scriptRevision").GetString().ShouldBe("1");
        body.RootElement.GetProperty("scriptPackage").ValueKind.ShouldBe(JsonValueKind.Object);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(value => value.Id == grant.Id);
        persisted.QueryCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreateDirectGrant_PersistsOneGrantForScriptAndAllReferences()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var package = PackageContract(setup, grantId);

        var response = await setup.UserClient.PostAsJsonAsync(
            $"api/vaults/{setup.VaultId}/grants",
            new CreateGrantRequest
            {
                GrantId = grantId,
                VaultId = setup.VaultId,
                AgentId = setup.AgentId,
                Type = GrantType.ScriptExecution,
                ScriptEntryId = setup.ScriptEntryId,
                ScriptPackage = package,
                Methods = GrantMethods.Exec,
                QueryLimit = 5,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var grants = await readContext.Grants.OfType<ScriptExecutionGrant>()
            .Include(value => value.ScriptExecutionPackage)
            .Include(value => value.ScriptExecutionScopes)
            .Where(value => value.AgentId == setup.AgentId && value.VaultId == setup.VaultId)
            .ToListAsync();
        grants.Count.ShouldBe(1);
        var direct = grants.Single();
        direct.Id.ShouldBe(grantId);
        direct.ScriptExecutionScopes.Count.ShouldBe(2);
        direct.Covers(setup.ScriptEntryId).ShouldBeTrue();
        direct.Covers(setup.ReferenceEntryId).ShouldBeFalse();

        var grantHttpResponse = await setup.UserClient.GetAsync(
            $"api/vaults/{setup.VaultId}/grants/{grantId}",
            TestContext.Current.CancellationToken);
        grantHttpResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var grantBody = JsonDocument.Parse(await grantHttpResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        grantBody.RootElement.GetProperty("scriptPackageRevision").GetString().ShouldBe("1");
        var scriptScopes = grantBody.RootElement.GetProperty("scriptScopes");
        scriptScopes.GetArrayLength().ShouldBe(2);
        scriptScopes.EnumerateArray().Single(scope => scope.GetProperty("isScript").GetBoolean())
            .GetProperty("entryId").GetGuid().ShouldBe(setup.ScriptEntryId);

        var deliveryResponse = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);
        deliveryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var deliveryBody = JsonDocument.Parse(await deliveryResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        var deliveredPackage = deliveryBody.RootElement.GetProperty("scriptPackage");
        deliveredPackage.GetProperty("producerSignature").GetString().ShouldBe(package.ProducerSignature);
        deliveredPackage.GetProperty("scopes").EnumerateArray()
            .Select(scope => scope.GetProperty("entryId").GetGuid())
            .ShouldBe(package.Scopes.Select(scope => scope.EntryId), ignoreOrder: false);
    }

    [Fact]
    public async Task CreateDirectGrant_RejectsNonCanonicalSignedScopeOrder()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var canonical = PackageContract(setup, grantId);
        var nonCanonical = SignPackage(canonical with
        {
            ProducerSignature = string.Empty,
            Scopes = canonical.Scopes.Reverse().ToArray(),
        });

        var response = await setup.UserClient.PostAsJsonAsync(
            $"api/vaults/{setup.VaultId}/grants",
            new CreateGrantRequest
            {
                GrantId = grantId,
                VaultId = setup.VaultId,
                AgentId = setup.AgentId,
                Type = GrantType.ScriptExecution,
                ScriptEntryId = setup.ScriptEntryId,
                ScriptPackage = nonCanonical,
                Methods = GrantMethods.Exec,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.AnyAsync(value => value.Id == grantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateDirectGrant_RejectsPackageWithInvalidVaultProducerSignature()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var signed = PackageContract(setup, grantId);
        var signature = WebEncoders.Base64UrlDecode(signed.ProducerSignature);
        signature[0] ^= 0x01;

        var response = await setup.UserClient.PostAsJsonAsync(
            $"api/vaults/{setup.VaultId}/grants",
            new CreateGrantRequest
            {
                GrantId = grantId,
                VaultId = setup.VaultId,
                AgentId = setup.AgentId,
                Type = GrantType.ScriptExecution,
                ScriptEntryId = setup.ScriptEntryId,
                ScriptPackage = signed with
                {
                    ProducerSignature = WebEncoders.Base64UrlEncode(signature),
                },
                Methods = GrantMethods.Exec,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.AnyAsync(value => value.Id == grantId)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReferencedEntryUpdate_RefreshesWholePackageWithoutReplacingGrantLifecycle()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            setup.OrganizationId,
            setup.VaultId,
            setup.ReferenceEntryId,
            baseRevision: 1) with
        {
            ScriptGrantPackages =
            [
                PackageContract(
                    setup,
                    grant.Id,
                    scriptRevision: 1,
                    packageRevision: 2,
                    referenceRevision: 2),
            ],
        };

        var response = await setup.UserClient.PutAsJsonAsync(
            $"api/vaults/{setup.VaultId}/entries/{setup.ReferenceEntryId}",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.OfType<ScriptExecutionGrant>()
            .Include(value => value.ScriptExecutionPackage)
            .Include(value => value.ScriptExecutionScopes)
            .SingleAsync(value => value.Id == grant.Id);
        persisted.Id.ShouldBe(grant.Id);
        persisted.QueryCount.ShouldBe(0);
        persisted.QueryLimit.ShouldBe(5);
        persisted.Status.ShouldBe(GrantStatus.Active);
        persisted.ScriptExecutionPackage!.PackageRevision.ShouldBe(2UL);
        persisted.ScriptExecutionScopes.Single(value => value.EntryId == setup.ReferenceEntryId)
            .EntryRevision.ShouldBe(2UL);
    }

    [Fact]
    public async Task ScriptConversion_AtomicallyRevokesItsDirectExecutionGrant()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);
        var request = EntryEnvelopeFaker.CreateUpdateRequest(
            setup.OrganizationId,
            setup.VaultId,
            setup.ScriptEntryId,
            baseRevision: 1) with
        {
            DeliveryPolicy = GrantDeliveryPolicy.Standard,
            RevokedScriptGrantIds = [grant.Id],
        };

        var response = await setup.UserClient.PutAsJsonAsync(
            $"api/vaults/{setup.VaultId}/entries/{setup.ScriptEntryId}",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.OfType<ScriptExecutionGrant>()
            .Include(value => value.ScriptExecutionPackage)
            .SingleAsync(value => value.Id == grant.Id);
        persisted.Status.ShouldBe(GrantStatus.Revoked);
        persisted.RevokedBySystem.ShouldBeTrue();
        persisted.ScriptExecutionPackage.ShouldBeNull();
        (await readContext.Entries.SingleAsync(value => value.Id == setup.ScriptEntryId))
            .DeliveryPolicy.ShouldBe(GrantDeliveryPolicy.Standard);
    }

    [Fact]
    public async Task DirectGrant_WithOneRemainingUse_AllowsExactlyOneConcurrentPackage()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 1);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);
        var route = $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package";
        var body = new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" };

        var responses = await Task.WhenAll(
            setup.Client.PostAsJsonAsync(route, body, TestContext.Current.CancellationToken),
            setup.Client.PostAsJsonAsync(route, body, TestContext.Current.CancellationToken));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests).ShouldBe(1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(value => value.Id == grant.Id);
        persisted.QueryCount.ShouldBe(1);
        persisted.Status.ShouldBe(GrantStatus.Consumed);
        (await readContext.ScriptExecutionPackages.AnyAsync(value => value.GrantId == grant.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task FullExec_ReturnsCurrentVaultCiphertextsWithoutCreatingScriptGrant()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId,
            setup.VaultId,
            setup.OrganizationId,
            setup.AgentId,
            setup.PublicKey,
            GrantEnvelopeTestData.AgentVaultKey(
                setup.OrganizationId, setup.VaultId, grantId, setup.AgentId,
                agentPublicKey: setup.PublicKey),
            null,
            4,
            "uses",
            GrantMethods.Exec,
            Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(),
            1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("authorizationSource").GetString().ShouldBe("full");
        body.RootElement.GetProperty("scriptPackage").ValueKind.ShouldBe(JsonValueKind.Null);
        body.RootElement.GetProperty("agentWrappedVaultKey").ValueKind.ShouldBe(JsonValueKind.Object);
        body.RootElement.GetProperty("vaultEntries").GetArrayLength().ShouldBe(2);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.OfType<ScriptExecutionGrant>().AnyAsync(grant =>
            grant.AgentId == setup.AgentId && grant.VaultId == setup.VaultId)).ShouldBeFalse();
    }

    [Fact]
    public async Task FullWithoutExec_ReturnsMethodNotAllowedWithoutConsumingOrCreatingDirectGrant()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId,
            setup.VaultId,
            setup.OrganizationId,
            setup.AgentId,
            setup.PublicKey,
            GrantEnvelopeTestData.AgentVaultKey(
                setup.OrganizationId, setup.VaultId, grantId, setup.AgentId,
                agentPublicKey: setup.PublicKey),
            null,
            4,
            "uses",
            GrantMethods.Get,
            Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(),
            1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        var generalErrors = body.RootElement.GetProperty("errors")
            .GetProperty("generalErrors")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        generalErrors.Length.ShouldBe(1);
        generalErrors[0].ShouldBe("method-not-allowed");
        body.RootElement.TryGetProperty("scriptPackage", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("agentWrappedVaultKey", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("vaultEntries", out _).ShouldBeFalse();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(value => value.Id == grantId);
        persisted.QueryCount.ShouldBe(0);
        persisted.Status.ShouldBe(GrantStatus.Active);
        (await readContext.Grants.OfType<ScriptExecutionGrant>().AnyAsync(grant =>
            grant.AgentId == setup.AgentId && grant.VaultId == setup.VaultId)).ShouldBeFalse();
        (await readContext.ScriptExecutionPackages.AnyAsync(package =>
            package.OrganizationId == setup.OrganizationId
            && package.VaultId == setup.VaultId)).ShouldBeFalse();
    }

    [Fact]
    public async Task CreatingFullExec_SupersedesDirectScriptGrantAndDeletesItsPackage()
    {
        var setup = await SetupScriptAsync();
        var direct = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(direct);
        var fullGrantId = Guid.NewGuid();
        var request = new CreateGrantRequest
        {
            GrantId = fullGrantId,
            VaultId = setup.VaultId,
            AgentId = setup.AgentId,
            Type = GrantType.Full,
            Methods = GrantMethods.Exec,
            AgentWrappedVaultKey = GrantEnvelopeTestData.AgentVaultKeyContract(
                setup.OrganizationId,
                setup.VaultId,
                fullGrantId,
                setup.AgentId,
                agentPublicKey: setup.PublicKey),
        };

        var response = await setup.UserClient.PostAsJsonAsync(
            $"api/vaults/{setup.VaultId}/grants",
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.SingleAsync(value => value.Id == direct.Id)).Status
            .ShouldBe(GrantStatus.Revoked);
        (await readContext.ScriptExecutionPackages.AnyAsync(value => value.GrantId == direct.Id))
            .ShouldBeFalse();
        (await readContext.Grants.OfType<FullGrant>().AnyAsync(value => value.Id == fullGrantId))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task FullExec_WithStaleRecipientFingerprint_FailsWithoutConsumingGrant()
    {
        var setup = await SetupScriptAsync();
        var grantId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            grantId,
            setup.VaultId,
            setup.OrganizationId,
            setup.AgentId,
            setup.PublicKey,
            GrantEnvelopeTestData.AgentVaultKey(
                setup.OrganizationId, setup.VaultId, grantId, setup.AgentId),
            null,
            4,
            "uses",
            GrantMethods.Exec,
            Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(),
            1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.SingleAsync(value => value.Id == grantId)).QueryCount.ShouldBe(0);
    }

    [Fact]
    public async Task OverlappingDirectAndFullExec_FailsWithoutConsumingEitherGrant()
    {
        var setup = await SetupScriptAsync();
        var direct = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(direct);
        var fullId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            fullId, setup.VaultId, setup.OrganizationId, setup.AgentId, setup.PublicKey,
            GrantEnvelopeTestData.AgentVaultKey(
                setup.OrganizationId, setup.VaultId, fullId, setup.AgentId,
                agentPublicKey: setup.PublicKey),
            null, 5, "uses", GrantMethods.Exec, Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(), 1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.SingleAsync(value => value.Id == direct.Id)).QueryCount.ShouldBe(0);
        (await readContext.Grants.SingleAsync(value => value.Id == full.Id)).QueryCount.ShouldBe(0);
    }

    [Fact]
    public async Task StaleScriptRevision_FailsWithoutConsumingGrant()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "2" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Grants.SingleAsync(value => value.Id == grant.Id);
        persisted.QueryCount.ShouldBe(0);
        persisted.Status.ShouldBe(GrantStatus.Active);
    }

    [Fact]
    public async Task StaleReferenceScope_FailsWithoutReturningPartialMaterialOrConsumingGrant()
    {
        var setup = await SetupScriptAsync();
        var grant = DirectGrant(setup, queryLimit: 5, referenceRevision: 2);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(grant);

        var response = await setup.Client.PostAsJsonAsync(
            $"api/agent/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/execution-package",
            new { setup.VaultId, setup.ScriptEntryId, ScriptRevision = "1" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var responseText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseText.ShouldNotContain("scriptPackage");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Grants.SingleAsync(value => value.Id == grant.Id)).QueryCount.ShouldBe(0);
    }

    [Fact]
    public async Task AccessImpact_CountsOverlappingDirectAndFullAgentOnlyOnce()
    {
        var setup = await SetupScriptAsync();
        var direct = DirectGrant(setup, queryLimit: 5);
        await apiFactory.Services.SeedScriptExecutionGrantAsync(direct);
        var fullId = Guid.NewGuid();
        var full = FullGrant.CreateProactively(
            fullId, setup.VaultId, setup.OrganizationId, setup.AgentId, setup.PublicKey,
            GrantEnvelopeTestData.AgentVaultKey(
                setup.OrganizationId, setup.VaultId, fullId, setup.AgentId,
                agentPublicKey: setup.PublicKey),
            null, 5, "uses", GrantMethods.Exec, Guid.NewGuid(),
            new GrantNames("agent", null, "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(), 1);
        await apiFactory.Services.SeedFullGrantAsync(full);

        var response = await setup.UserClient.GetAsync(
            $"api/vaults/{setup.VaultId}/scripts/{setup.ScriptEntryId}/access-impact",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("effectiveAgentCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("directAgentCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("fullAgentCount").GetInt32().ShouldBe(1);
        body.RootElement.GetProperty("hasOverlappingCoverage").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("agentIds").EnumerateArray()
            .Select(value => value.GetGuid()).ShouldBe([setup.AgentId]);
    }

    private static ScriptExecutionGrant DirectGrant(
        Setup setup,
        int queryLimit,
        ulong referenceRevision = 1)
    {
        var grantId = Guid.NewGuid();
        var fingerprint = VaultKeyFingerprint.Compute(
            Convert.FromBase64String(setup.PublicKey), VaultKeyKind.AgentX25519);
        var package = ScriptExecutionPackage.Create(
            setup.OrganizationId,
            setup.VaultId,
            grantId,
            setup.AgentId,
            1,
            setup.ScriptEntryId,
            1,
            1,
            1,
            1,
            fingerprint,
            1,
            VaultKeyFingerprint.Compute(
                VaultTrustAnchorFaker.ManifestSigningPublicKey, VaultKeyKind.VaultSigningEd25519),
            Enumerable.Repeat((byte)0xA5, 32).ToArray(),
            Enumerable.Repeat((byte)0x5A, 64).ToArray(),
            new byte[64]);
        return ScriptExecutionGrant.CreateProactively(
            grantId,
            setup.VaultId,
            setup.OrganizationId,
            setup.AgentId,
            setup.PublicKey,
            setup.ScriptEntryId,
            [
                ScriptExecutionScope.Create(
                    setup.OrganizationId, setup.VaultId, grantId, setup.ScriptEntryId, 1, true),
                ScriptExecutionScope.Create(
                    setup.OrganizationId, setup.VaultId, grantId, setup.ReferenceEntryId,
                    referenceRevision, false),
            ],
            package,
            null,
            queryLimit,
            "uses",
            Guid.NewGuid(),
            new GrantNames("agent", "script", "vault", "actor"),
            SystemClock.Instance.GetCurrentInstant(),
            1);
    }

    private static ScriptExecutionPackageContract PackageContract(
        Setup setup,
        Guid grantId,
        ulong scriptRevision = 1,
        ulong packageRevision = 1,
        ulong referenceRevision = 1)
    {
        var unsigned = new ScriptExecutionPackageContract(
            1,
            setup.OrganizationId,
            setup.VaultId,
            grantId,
            setup.AgentId,
            1,
            setup.ScriptEntryId,
            scriptRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            packageRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            1,
            WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(
                Convert.FromBase64String(setup.PublicKey), VaultKeyKind.AgentX25519)),
            1,
            WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(
                VaultTrustAnchorFaker.ManifestSigningPublicKey, VaultKeyKind.VaultSigningEd25519)),
            WebEncoders.Base64UrlEncode(Enumerable.Repeat((byte)0xA5, 32).ToArray()),
            WebEncoders.Base64UrlEncode(Enumerable.Repeat((byte)0x5A, 64).ToArray()),
            string.Empty,
            new[]
            {
                new ScriptExecutionScopeContract(
                    setup.ScriptEntryId,
                    scriptRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    true),
                new ScriptExecutionScopeContract(
                    setup.ReferenceEntryId,
                    referenceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    false),
            }.OrderBy(scope => scope.EntryId.ToString("D"), StringComparer.Ordinal).ToArray());
        return SignPackage(unsigned);
    }

    private static ScriptExecutionPackageContract SignPackage(ScriptExecutionPackageContract unsigned)
    {
        var canonical = ScriptExecutionPackageCryptoValidator.CanonicalizeUnsigned(unsigned);
        var prefix = Encoding.ASCII.GetBytes("PLDNV2SIG:SCRIPT-EXECUTION-PACKAGE:");
        var signatureInput = new byte[prefix.Length + sizeof(ushort) + canonical.Length];
        prefix.CopyTo(signatureInput, 0);
        BinaryPrimitives.WriteUInt16BigEndian(signatureInput.AsSpan(prefix.Length), 2);
        canonical.CopyTo(signatureInput, prefix.Length + sizeof(ushort));
        using var signingKey = VaultTrustAnchorFaker.CreateManifestSigningKey();
        return unsigned with
        {
            ProducerSignature = WebEncoders.Base64UrlEncode(
                SignatureAlgorithm.Ed25519.Sign(signingKey, signatureInput)),
        };
    }

    private sealed record Setup(
        HttpClient Client,
        HttpClient UserClient,
        Guid OrganizationId,
        Guid VaultId,
        Guid ScriptEntryId,
        Guid ReferenceEntryId,
        Guid AgentId,
        string PublicKey,
        AgentRequestSigning Signing,
        string AgentMessageKeyFingerprint);
}
