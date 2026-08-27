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
        byte[] encodedSealedVaultKeyPackage)
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
        };
    }

    internal void ReplaceWith(AgentWrappedVaultKey replacement)
    {
        if (OrganizationId != replacement.OrganizationId
            || VaultId != replacement.VaultId
            || GrantId != replacement.GrantId
            || AgentId != replacement.AgentId
            || AgentAccessEpoch != replacement.AgentAccessEpoch
            || replacement.VaultKeyVersion.Value != checked(VaultKeyVersion.Value + 1))
        {
            throw new DomainException("Rotated Agent Vault-key wrapper does not preserve its recipient identity.");
        }

        ProtocolVersion = replacement.ProtocolVersion;
        WrapperSuiteId = replacement.WrapperSuiteId;
        VaultKeyVersion = replacement.VaultKeyVersion;
        RecipientAgentKeyVersion = replacement.RecipientAgentKeyVersion;
        RecipientAgentKeyFingerprint = replacement.RecipientAgentKeyFingerprint.ToArray();
        EncodedSealedVaultKeyPackage = replacement.EncodedSealedVaultKeyPackage.ToArray();
    }
}
