using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Module.Vault.Domain;

// One ciphertext wrapper per active FULL grant. The wrapped plaintext is the current Vault key;
// the server validates descriptor binding and stores only the sealed-box package.
internal sealed class AgentWrappedVaultKey
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid GrantId { get; private set; }
    public Guid AgentId { get; private set; }
    internal uint AgentAccessEpoch { get; private set; }
    internal ushort ProtocolVersion { get; private set; }
    internal string WrapperSuiteId { get; private set; } = string.Empty;
    internal VaultKeyVersion VaultKeyVersion { get; private set; }
    internal AgentRecipientKeyVersion RecipientAgentKeyVersion { get; private set; }
    internal byte[] RecipientAgentKeyFingerprint { get; private set; } = [];
    internal byte[] EncodedSealedVaultKeyPackage { get; private set; } = [];
    internal ManifestSigningKeyVersion VaultSigningKeyVersion { get; private set; }
    internal byte[] VaultSigningKeyFingerprint { get; private set; } = [];
    internal byte[] ProducerSignature { get; private set; } = [];

    private AgentWrappedVaultKey() { }

    internal static AgentWrappedVaultKey Create(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        ushort protocolVersion,
        string wrapperSuiteId,
        uint vaultKeyVersion,
        uint recipientAgentKeyVersion,
        byte[] recipientAgentKeyFingerprint,
        byte[] encodedSealedVaultKeyPackage,
        uint vaultSigningKeyVersion = 1,
        byte[]? vaultSigningKeyFingerprint = null,
        byte[]? producerSignature = null)
    {
        if (protocolVersion != VaultProtocol.CurrentVersion)
        {
            throw new DomainException("Agent Vault-key wrapper protocol is invalid.");
        }

        WrappedKeyPackageContract.ValidatePackage(wrapperSuiteId, encodedSealedVaultKeyPackage);
        var context = new X25519WrapperContext(
            X25519WrapperPurpose.AgentVaultKey,
            new EnvelopeScope(organizationId, vaultId, GrantOrRequestId: grantId, AgentId: agentId),
            agentAccessEpoch,
            vaultKeyVersion,
            null,
            VaultKeyKind.AgentX25519,
            recipientAgentKeyVersion,
            recipientAgentKeyFingerprint,
            null);
        _ = X25519WrapperContextCodec.Encode(context);
        X25519SealedBoxContract.ValidatePackage(encodedSealedVaultKeyPackage);
        if (protocolVersion != VaultProtocol.CurrentVersion
            || !string.Equals(wrapperSuiteId, X25519SealedBoxContract.SuiteId, StringComparison.Ordinal))
        {
            throw new DomainException("Agent Vault-key wrapper suite is invalid.");
        }
        vaultSigningKeyFingerprint ??= new byte[VaultProtocol.FingerprintBytes];
        producerSignature ??= new byte[64];
        if (vaultSigningKeyVersion == 0
            || vaultSigningKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || producerSignature.Length != 64)
        {
            throw new DomainException("Agent Vault-key wrapper producer binding is invalid.");
        }

        return new AgentWrappedVaultKey
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            GrantId = grantId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            ProtocolVersion = protocolVersion,
            WrapperSuiteId = wrapperSuiteId,
            VaultKeyVersion = new VaultKeyVersion(vaultKeyVersion),
            RecipientAgentKeyVersion = new AgentRecipientKeyVersion(recipientAgentKeyVersion),
            RecipientAgentKeyFingerprint = recipientAgentKeyFingerprint.ToArray(),
            EncodedSealedVaultKeyPackage = encodedSealedVaultKeyPackage.ToArray(),
            VaultSigningKeyVersion = new ManifestSigningKeyVersion(vaultSigningKeyVersion),
            VaultSigningKeyFingerprint = vaultSigningKeyFingerprint.ToArray(),
            ProducerSignature = producerSignature.ToArray(),
        };
    }

    internal void ReplaceWith(AgentWrappedVaultKey replacement)
    {
        var rotatesVaultKey = replacement.VaultKeyVersion.Value == checked(VaultKeyVersion.Value + 1)
                              && replacement.VaultSigningKeyVersion.Value >= VaultSigningKeyVersion.Value;
        var rotatesSigningKey = replacement.VaultKeyVersion == VaultKeyVersion
                                && replacement.VaultSigningKeyVersion.Value
                                == checked(VaultSigningKeyVersion.Value + 1);
        if (OrganizationId != replacement.OrganizationId
            || VaultId != replacement.VaultId
            || GrantId != replacement.GrantId
            || AgentId != replacement.AgentId
            || AgentAccessEpoch != replacement.AgentAccessEpoch
            || !(rotatesVaultKey || rotatesSigningKey))
        {
            throw new DomainException("Rotated Agent Vault-key wrapper does not preserve its recipient identity.");
        }

        ProtocolVersion = replacement.ProtocolVersion;
        WrapperSuiteId = replacement.WrapperSuiteId;
        VaultKeyVersion = replacement.VaultKeyVersion;
        RecipientAgentKeyVersion = replacement.RecipientAgentKeyVersion;
        RecipientAgentKeyFingerprint = replacement.RecipientAgentKeyFingerprint.ToArray();
        EncodedSealedVaultKeyPackage = replacement.EncodedSealedVaultKeyPackage.ToArray();
        VaultSigningKeyVersion = replacement.VaultSigningKeyVersion;
        VaultSigningKeyFingerprint = replacement.VaultSigningKeyFingerprint.ToArray();
        ProducerSignature = replacement.ProducerSignature.ToArray();
    }
}
