using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Shared;

[PublicAPI]
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record AgentVaultDiscoveryEnvelopeContract(
    ushort ProtocolVersion,
    Guid OrganizationId,
    Guid VaultId,
    Guid AgentId,
    uint VdkVersion,
    X25519WrappedKeyContract WrappedVdk,
    string ManifestRevision,
    string ManifestSignature)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public uint RecipientAgentKeyVersion => WrappedVdk.Descriptor.RecipientKeyVersion;
    [System.Text.Json.Serialization.JsonIgnore]
    public string RecipientAgentKeyFingerprint => WrappedVdk.Descriptor.RecipientFingerprint;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AgentWrappedVdk => WrappedVdk.EncodedSealedKeyPackage;

    public AgentVaultDiscoveryEnvelopeContract(
        ushort protocolVersion, Guid organizationId, Guid vaultId, Guid agentId, uint vdkVersion,
        ushort _, uint recipientKeyVersion, string recipientFingerprint, string encodedPackage,
        string manifestRevision, string manifestSignature)
        : this(protocolVersion, organizationId, vaultId, agentId, vdkVersion,
            new X25519WrappedKeyContract(
                new X25519WrapperDescriptorContract(protocolVersion,
                    "palladin-x25519-sealed-box-v1", X25519WrapperPurposeContract.AgentDiscoveryVdk,
                    new EnvelopeScopeContract(organizationId, vaultId, AgentId: agentId),
                    vdkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), vdkVersion, null,
                    X25519RecipientKeyKindContract.AgentX25519, recipientKeyVersion,
                    recipientFingerprint, null), encodedPackage), manifestRevision, manifestSignature) { }
}

[PublicAPI]
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record VaultManifestContract(
    ushort ProtocolVersion,
    string CryptoSuiteId,
    string WrapperSuiteId,
    string SignatureSuiteId,
    Guid OrganizationId,
    Guid VaultId,
    Guid AgentId,
    string AgentX25519Fingerprint,
    string AgentEd25519Fingerprint,
    string VaultSigningPublicKey,
    string VaultSigningKeyFingerprint,
    uint ManifestSigningKeyVersion,
    string VaultAgentMessagePublicKey,
    string VaultAgentMessageKeyFingerprint,
    uint AgentMessageKeyVersion,
    uint VdkVersion,
    string AgentWrappedVdkDigest,
    string ManifestRevision,
    Instant IssuedAt,
    ushort MinimumAgentRuntimeProtocol,
    string Signature)
{
    public VaultManifestContract(
        ushort protocolVersion, ushort _, Guid organizationId, Guid vaultId, Guid agentId,
        string agentX25519Fingerprint, string agentEd25519Fingerprint, string vaultSigningPublicKey,
        string vaultSigningKeyFingerprint, uint manifestSigningKeyVersion,
        string vaultAgentMessagePublicKey, string vaultAgentMessageKeyFingerprint,
        uint agentMessageKeyVersion, uint vdkVersion, string agentWrappedVdkDigest,
        string manifestRevision, Instant issuedAt, ushort minimumAgentRuntimeProtocol, string signature)
        : this(protocolVersion, "palladin-vault-xchacha-v1", "palladin-x25519-sealed-box-v1",
            "palladin-ed25519-v1", organizationId, vaultId, agentId, agentX25519Fingerprint,
            agentEd25519Fingerprint, vaultSigningPublicKey, vaultSigningKeyFingerprint,
            manifestSigningKeyVersion, vaultAgentMessagePublicKey, vaultAgentMessageKeyFingerprint,
            agentMessageKeyVersion, vdkVersion, agentWrappedVdkDigest, manifestRevision, issuedAt,
            minimumAgentRuntimeProtocol, signature) { }
}
