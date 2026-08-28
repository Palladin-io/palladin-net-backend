using System.Text.Json;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Microsoft.AspNetCore.WebUtilities;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EnvelopeDescriptorCodecTests
{
    [Theory]
    [InlineData(VaultPublicKeyKindContract.AgentMessageX25519, "palladin-x25519-v1", 4)]
    [InlineData(VaultPublicKeyKindContract.ManifestSigningEd25519, "palladin-ed25519-v1", 3)]
    public void VaultPublicKey_ValidatesDomainSeparatedFingerprintAndVersion(
        VaultPublicKeyKindContract kind, string scheme, int fingerprintKind)
    {
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var contract = new VaultPublicKeyContract(2, scheme, kind, 7,
            WebEncoders.Base64UrlEncode(key),
            WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(key, (VaultKeyKind)fingerprintKind)));

        var result = VaultEnvelopeContractMapper.ToDomain(contract, kind, 7);

        result.PublicKey.ShouldBe(key);
        result.Version.ShouldBe(7u);
    }

    [Fact]
    public void VaultPublicKey_WithFingerprintFromDifferentKeyKind_FailsClosed()
    {
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var contract = new VaultPublicKeyContract(2, "palladin-x25519-v1",
            VaultPublicKeyKindContract.AgentMessageX25519, 1, WebEncoders.Base64UrlEncode(key),
            WebEncoders.Base64UrlEncode(VaultKeyFingerprint.Compute(key, VaultKeyKind.VaultSigningEd25519)));

        var action = () => VaultEnvelopeContractMapper.ToDomain(
            contract, VaultPublicKeyKindContract.AgentMessageX25519, 1);

        action.ShouldThrow<DomainException>().Message.ShouldContain("fingerprint");
    }

    [Fact]
    public void GrantDescriptor_MatchesFrozenCrossClientVector()
    {
        var descriptor = new EnvelopeDescriptor(
            2,
            new CryptoSuiteId(CryptoSuiteId.XChaCha20Poly1305V1),
            EnvelopePurpose.GrantPayload,
            new EnvelopeScope(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                Guid.Parse("11112222-3333-4444-8555-666677778888"),
                Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
                Guid.Parse("12345678-1234-4234-8234-1234567890ab"),
                Guid.Parse("fedcba98-7654-4321-8765-abcdefabcdef")),
            7,
            3,
            9,
            new GrantEnvelopeBinding(
                6,
                X25519SealedBoxContract.SuiteId,
                4,
                Enumerable.Repeat((byte)0x5a, 32).ToArray(),
                3,
                (ushort)GrantDeliveryPolicy.Standard,
                Enumerable.Repeat((byte)0xa5, 32).ToArray(),
                1_700_000_000,
                123_456_789,
                5));

        Convert.ToHexStringLower(EnvelopeDescriptorCodec.Encode(descriptor)).ShouldBe(
            "504c444e454e56320002001970616c6c6164696e2d7661756c742d786368616368612d7631" +
            "000a001f00112233445566778899aabbccddeeff11112222333344448555666677778888" +
            "aaaaaaaabbbb4ccc8dddeeeeeeeeeeee123456781234423482341234567890abfedcba98765443218765abcdefabcdef" +
            "00000000000000070000000301000000090000000000000006001d70616c6c6164696e2d7832353531392d7365616c65642d626f782d7631" +
            "000000045a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a00030000" +
            "a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5" +
            "01000000006553f100075bcd150100000005");
    }

    [Fact]
    public void FieldSetCommitment_IsSortedAndMatchesFrozenVector()
    {
        var first = EnvelopeDescriptorCodec.ComputeFieldSetCommitment(["username", "password"]);
        var second = EnvelopeDescriptorCodec.ComputeFieldSetCommitment(["password", "username"]);

        first.ShouldBe(second);
        Convert.ToHexStringLower(first).ShouldBe(
            "f4efe1b64791880d2f2bcd96905ae75bb39a8c5212a9b77831e9376699372708");
    }

    [Fact]
    public void FieldSetCommitment_RejectsDuplicateIdentifiers()
    {
        var act = () => EnvelopeDescriptorCodec.ComputeFieldSetCommitment(["password", "password"]);
        act.ShouldThrow<DomainException>();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("zażółć")]
    [InlineData("line\nbreak")]
    public void FieldSetCommitment_RejectsNonCanonicalIdentifiers(string fieldId)
    {
        var act = () => EnvelopeDescriptorCodec.ComputeFieldSetCommitment([fieldId]);
        act.ShouldThrow<DomainException>();
    }

    [Fact]
    public void PurposeScopeMismatch_FailsClosed()
    {
        var descriptor = new EnvelopeDescriptor(
            2,
            new CryptoSuiteId(CryptoSuiteId.XChaCha20Poly1305V1),
            EnvelopePurpose.MemberVaultMetadata,
            new EnvelopeScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            1,
            1,
            1,
            new EmptyEnvelopeBinding());

        var act = () => EnvelopeDescriptorCodec.Encode(descriptor);
        act.ShouldThrow<DomainException>();
    }

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, true)]
    [InlineData(41, false)]
    [InlineData(65, false)]
    public void XChaChaSuite_EnforcesPayloadBound(int length, bool valid)
    {
        var suite = new XChaCha20Poly1305VaultEnvelopeSuite();
        var act = () => suite.ValidateEncodedPayload(
            EnvelopePurpose.MemberVaultMetadata,
            new EncodedSuitePayload(new byte[length]),
            16);

        if (valid) act.ShouldNotThrow();
        else act.ShouldThrow<DomainException>();
    }

    [Fact]
    public void WrappedKeyPackageContract_DispatchesValidationBySuite()
    {
        WrappedKeyPackageContract.ValidatePackage(X25519SealedBoxContract.SuiteId, new byte[120]);

        Should.Throw<DomainException>(() => WrappedKeyPackageContract.ValidatePackage(
            X25519SealedBoxContract.SuiteId, new byte[80]));
        Should.Throw<DomainException>(() => WrappedKeyPackageContract.ValidatePackage(
            "unsupported-suite", new byte[120]));
    }

    [Fact]
    public void SharedEnvelopeFixture_MatchesDescriptorAndKdfCodecs()
    {
        using var fixture = LoadFixture("envelope-xchacha-hkdf.json");
        var root = fixture.RootElement;
        var value = root.GetProperty("descriptor");
        var scope = Scope(value);
        var descriptor = GrantDescriptor(value, scope);

        Convert.ToHexStringLower(EnvelopeDescriptorCodec.Encode(descriptor)).ShouldBe(
            root.GetProperty("expected").GetProperty("descriptorAadHex").GetString());
        Convert.ToHexStringLower(VaultKdfContextCodec.Encode(
                descriptor.CryptoSuiteId, descriptor.Purpose, scope,
                descriptor.KeyVersion, descriptor.MemberKeyGeneration))
            .ShouldBe(root.GetProperty("expected").GetProperty("kdfContextHex").GetString());
    }

    [Fact]
    public void SharedWrapperFixture_MatchesContextCodecAndFrozenPackageSize()
    {
        using var fixture = LoadFixture("x25519-sealed-box.json");
        var root = fixture.RootElement;
        var value = root.GetProperty("context");
        var context = new X25519WrapperContext(
            (X25519WrapperPurpose)value.GetProperty("purpose").GetUInt16(), Scope(value),
            value.GetProperty("resourceRevision").GetUInt64(),
            value.GetProperty("wrappedKeyVersion").GetUInt32(),
            value.GetProperty("memberKeyGeneration").GetUInt32(),
            (VaultKeyKind)value.GetProperty("recipientKeyKind").GetUInt16(),
            value.GetProperty("recipientKeyVersion").GetUInt32(),
            Convert.FromHexString(value.GetProperty("recipientFingerprintHex").GetString()!),
            Convert.FromHexString(value.GetProperty("parentDescriptorHashHex").GetString()!));

        Convert.ToHexStringLower(X25519WrapperContextCodec.Encode(context)).ShouldBe(
            root.GetProperty("expectedContextHex").GetString());
        WrappedKeyPackageContract.ValidatePackage(X25519SealedBoxContract.SuiteId,
            Convert.FromHexString(root.GetProperty("sealedPackageHex").GetString()!));
    }

    private static EnvelopeDescriptor GrantDescriptor(JsonElement value, EnvelopeScope scope) => new(
        value.GetProperty("protocolVersion").GetUInt16(),
        new CryptoSuiteId(value.GetProperty("cryptoSuiteId").GetString()!),
        (EnvelopePurpose)value.GetProperty("purpose").GetUInt16(), scope,
        value.GetProperty("resourceRevision").GetUInt64(), value.GetProperty("keyVersion").GetUInt32(),
        value.GetProperty("memberKeyGeneration").GetUInt32(),
        new GrantEnvelopeBinding(
            value.GetProperty("entryRevision").GetUInt64(), value.GetProperty("wrapperSuiteId").GetString()!,
            value.GetProperty("recipientKeyVersion").GetUInt32(),
            Convert.FromHexString(value.GetProperty("recipientFingerprintHex").GetString()!),
            value.GetProperty("approvedMethods").GetUInt16(),
            value.GetProperty("deliveryPolicy").GetUInt16(),
            Convert.FromHexString(value.GetProperty("fieldSetCommitmentHex").GetString()!),
            value.GetProperty("expiresAtUnixSeconds").GetInt64(),
            value.GetProperty("expiresAtNanoseconds").GetUInt32(), value.GetProperty("remainingUses").GetUInt32()));

    private static EnvelopeScope Scope(JsonElement value) => new(
        Guid.Parse(value.GetProperty("organizationId").GetString()!),
        Guid.Parse(value.GetProperty("vaultId").GetString()!),
        value.TryGetProperty("entryId", out var entry) ? Guid.Parse(entry.GetString()!) : null,
        value.TryGetProperty("grantOrRequestId", out var grant) ? Guid.Parse(grant.GetString()!) : null,
        value.TryGetProperty("agentId", out var agent) ? Guid.Parse(agent.GetString()!) : null,
        value.TryGetProperty("memberId", out var member) ? Guid.Parse(member.GetString()!) : null);

    private static JsonDocument LoadFixture(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "VaultProtocol2", name)));
}
