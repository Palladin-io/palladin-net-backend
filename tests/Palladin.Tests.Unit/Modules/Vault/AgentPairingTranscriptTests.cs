using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class AgentPairingTranscriptTests
{
    [Fact]
    public void When_FrozenPairingVectorIsCanonicalized_Then_DigestMatchesByteForByte()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "VaultProtocol2",
            "pairing.json")));
        var root = fixture.RootElement;
        var vector = root.GetProperty("vectors")[0];
        var transcript = vector.GetProperty("transcript");
        var manifests = root.GetProperty("signedManifests")
            .EnumerateArray()
            .Select(ToManifest)
            .ToList();

        var digest = AgentPairingTranscriptService.ComputeDigest(
            transcript.GetProperty("activationId").GetGuid(),
            transcript.GetProperty("organizationId").GetGuid(),
            transcript.GetProperty("agentId").GetGuid(),
            WebEncoders.Base64UrlDecode(transcript.GetProperty("agentX25519Fingerprint").GetString()!),
            WebEncoders.Base64UrlDecode(transcript.GetProperty("agentEd25519Fingerprint").GetString()!),
            manifests);

        Convert.ToHexString(digest).ToLowerInvariant()
            .ShouldBe(vector.GetProperty("digestHex").GetString());
    }

    [Fact]
    public void When_ConfirmationDigestDiffers_Then_FailsClosed()
    {
        var now = Instant.FromUtc(2026, 7, 26, 12, 0);
        var activation = CreateActivation(now);

        var action = () => activation.Confirm(Guid.NewGuid(), new byte[32], now);

        action.ShouldThrow<DomainException>().Message.ShouldContain("does not match");
        activation.ConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public void When_ActivationExpired_Then_ConfirmationFailsWithoutMutation()
    {
        var now = Instant.FromUtc(2026, 7, 26, 12, 0);
        var activation = CreateActivation(now);

        var action = () => activation.Confirm(
            Guid.NewGuid(),
            Enumerable.Repeat((byte)7, 32).ToArray(),
            now + Duration.FromMinutes(10));

        action.ShouldThrow<DomainException>().Message.ShouldContain("expired");
        activation.ConfirmedAt.ShouldBeNull();
    }

    [Fact]
    public void When_SameMemberRetriesIdenticalConfirmation_Then_IsIdempotent()
    {
        var now = Instant.FromUtc(2026, 7, 26, 12, 0);
        var memberId = Guid.NewGuid();
        var digest = Enumerable.Repeat((byte)7, 32).ToArray();
        var activation = CreateActivation(now);

        activation.Confirm(memberId, digest, now + Duration.FromMinutes(1));
        activation.Confirm(memberId, digest, now + Duration.FromMinutes(2));

        activation.ConfirmedBy.ShouldBe(memberId);
        activation.ConfirmedAt.ShouldBe(now + Duration.FromMinutes(1));
    }

    private static AgentPairingActivation CreateActivation(Instant now) =>
        AgentPairingActivation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new byte[32],
            new byte[32],
            Enumerable.Repeat((byte)7, 32).ToArray(),
            2,
            now,
            Duration.FromMinutes(10));

    private static VaultManifestContract ToManifest(JsonElement value) => new(
        value.GetProperty("protocolVersion").GetUInt16(),
        value.GetProperty("cryptoSuiteId").GetString()!,
        value.GetProperty("wrapperSuiteId").GetString()!,
        value.GetProperty("signatureSuiteId").GetString()!,
        value.GetProperty("organizationId").GetGuid(),
        value.GetProperty("vaultId").GetGuid(),
        value.GetProperty("agentId").GetGuid(),
        value.GetProperty("agentX25519Fingerprint").GetString()!,
        value.GetProperty("agentEd25519Fingerprint").GetString()!,
        value.GetProperty("vaultSigningPublicKey").GetString()!,
        value.GetProperty("vaultSigningKeyFingerprint").GetString()!,
        value.GetProperty("manifestSigningKeyVersion").GetUInt32(),
        value.GetProperty("vaultAgentMessagePublicKey").GetString()!,
        value.GetProperty("vaultAgentMessageKeyFingerprint").GetString()!,
        value.GetProperty("agentMessageKeyVersion").GetUInt32(),
        value.GetProperty("vdkVersion").GetUInt32(),
        value.GetProperty("agentWrappedVdkDigest").GetString()!,
        value.GetProperty("manifestRevision").GetString()!,
        Instant.FromDateTimeOffset(DateTimeOffset.Parse(
            value.GetProperty("issuedAt").GetString()!,
            CultureInfo.InvariantCulture)),
        value.GetProperty("minimumAgentRuntimeProtocol").GetUInt16(),
        value.GetProperty("signature").GetString()!);
}
