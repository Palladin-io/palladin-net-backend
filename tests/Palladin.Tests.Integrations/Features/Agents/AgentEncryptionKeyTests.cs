using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using MassTransit.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Crypto;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class AgentEncryptionKeyTests(ApiFactory apiFactory) : TestBase
{
    private const string LegacySuite = "palladin-x25519-sealed-box-v1";

    [Fact]
    public async Task When_AgentSignsANewXWingKey_Then_ItBecomesTheNextRecipientKeyVersionIdempotently()
    {
        // Given
        var agent = await SeedAgentAsync();
        var next = XWingTestKeys.CreateKeyPair().PublicKey;
        var request = RotationRequest(agent, next, agent.Signing);

        // When
        var response = await agent.Client.PostAsJsonAsync("api/agent/encryption-key", request);
        var retry = await agent.Client.PostAsJsonAsync("api/agent/encryption-key", request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = (await response.Content.ReadFromJsonAsync<AgentEncryptionKeyStatusResponse>())!;
        status.WrapperSuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        status.KeyVersion.ShouldBe(2u);
        status.KeyFingerprint.ShouldBe(WebEncoders.Base64UrlEncode(KeyWrapperSuites.Current.Fingerprint(1, next)));
        status.PreviousKeyEnvelopeCount.ShouldBeNull();
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<AgentEncryptionKeyStatusResponse>())!.KeyVersion.ShouldBe(2u);
        (await apiFactory.Services.GetRequiredService<ITestHarness>().Published.Any<AgentUpsertedEvent>(
                x => x.Context.Message.AgentId == agent.Id && x.Context.Message.RecipientKeyVersion == 2
                     && x.Context.Message.EncryptionKeySuiteId == KeyWrapperSuites.Current.Id
                     && x.Context.Message.EncryptionPublicKey == Convert.ToBase64String(next)
                     && x.Context.Message.PublicKey == agent.IdentityKey,
                TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_BindingIsSignedByAnotherKey_Then_ForbiddenAndKeyUnchanged()
    {
        // Given
        var agent = await SeedAgentAsync();
        var next = XWingTestKeys.CreateKeyPair().PublicKey;

        // When
        var response = await agent.Client.PostAsJsonAsync("api/agent/encryption-key",
            RotationRequest(agent, next, AgentRequestSigning.Generate()));
        var unchanged = await agent.Client.PostAsJsonAsync("api/agent/encryption-key",
            RotationRequest(agent, XWingTestKeys.CreateKeyPair().PublicKey, agent.Signing));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldContain("key-binding-signature-invalid");
        (await unchanged.Content.ReadFromJsonAsync<AgentEncryptionKeyStatusResponse>())!.KeyVersion.ShouldBe(2u);
    }

    [Fact]
    public async Task When_NewKeyUsesTheReadOnlySuite_Then_RejectedAsMalformed()
    {
        // Given
        var agent = await SeedAgentAsync();
        var next = new byte[32];
        Random.Shared.NextBytes(next);

        // When
        var response = await agent.Client.PostAsJsonAsync("api/agent/encryption-key",
            RotationRequest(agent, next, agent.Signing, LegacySuite));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("malformed-agent-encryption-key");
    }

    [Fact]
    public async Task When_GrantEnvelopeAddressesAnOlderAgentKey_Then_StatusCountsIt()
    {
        // Given
        var agent = await SeedAgentAsync();
        var encryptionKey = VaultAgentFaker.GenerateEncryptionPublicKey();
        await apiFactory.Services.SeedVaultAgentAsync(agent.OrganizationId, id: agent.Id, publicKey: encryptionKey,
            signingPublicKey: agent.Signing.PublicKeyBase64, recipientKeyVersion: 2);
        var vault = await apiFactory.Services.SeedVaultAsync(agent.OrganizationId, agent.OwnerId);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, agent.OwnerId,
            EntryFaker.Create(organizationId: agent.OrganizationId, vaultId: vault.Id, createdBy: agent.OwnerId));
        var grant = GrantFaker.CreateGranular(
            vaultId: vault.Id, organizationId: agent.OrganizationId, agentId: agent.Id, entryId: entry.Id).Generate();
        grant.GrantEntryScopes.Add(GrantEnvelopeTestData.Scope(
            agent.OrganizationId, vault.Id, grant.Id, entry.Id, grant.Methods, agentId: agent.Id));
        await apiFactory.Services.SeedGranularGrantAsync(grant);

        // When
        var status = await agent.Client.GetFromJsonAsync<AgentEncryptionKeyStatusResponse>("api/agent/encryption-key");

        // Then
        status!.WrapperSuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        status.KeyVersion.ShouldBe(2u);
        status.KeyFingerprint.ShouldBe(WebEncoders.Base64UrlEncode(
            KeyWrapperSuites.Current.Fingerprint(1, Convert.FromBase64String(encryptionKey))));
        status.PreviousKeyEnvelopeCount.ShouldBe(1UL);
    }

    private async Task<SeededAgent> SeedAgentAsync()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var organizationId = organization.Id;
        var (_, apiKey) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        var signing = AgentRequestSigning.Generate();
        var identityKey = AgentFaker.GeneratePublicKey();
        var agent = await apiFactory.Services.SeedAgentAsync(organizationId,
            AgentFaker.Create(organizationId: organizationId, publicKey: identityKey, signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        return new SeededAgent(agent.Id, organizationId, owner.Id, identityKey, signing,
            apiFactory.CreateSignedAgentClient(agent.Id, apiKey, identityKey, signing));
    }

    // palladin.agent-encryption-key-rotation.v1 || 0x00 || u16be(2) || agentId || previousFp || u16be(len) || suite || nextFp
    private static RotateAgentEncryptionKeyRequest RotationRequest(
        SeededAgent agent, byte[] nextKey, AgentRequestSigning signer, string? suiteId = null)
    {
        var suite = suiteId ?? KeyWrapperSuites.Current.Id;
        var previous = KeyWrapperSuites.Resolve(LegacySuite).Fingerprint(1, Convert.FromBase64String(agent.IdentityKey));
        var next = suiteId is null ? KeyWrapperSuites.Current.Fingerprint(1, nextKey) : new byte[32];
        var suiteBytes = Encoding.ASCII.GetBytes(suite);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)suiteBytes.Length);
        byte[] statement =
        [
            .. "palladin.agent-encryption-key-rotation.v1"u8, 0, 0, 2,
            .. agent.Id.ToByteArray(bigEndian: true), .. previous, .. length, .. suiteBytes, .. next,
        ];
        return new RotateAgentEncryptionKeyRequest
        {
            WrapperSuiteId = suite,
            PublicKey = WebEncoders.Base64UrlEncode(nextKey),
            KeyBindingSignature = signer.SignBytesBase64Url(statement),
        };
    }

    private sealed record SeededAgent(
        Guid Id, Guid OrganizationId, Guid OwnerId, string IdentityKey, AgentRequestSigning Signing, HttpClient Client);
}
