using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class ScriptExecutionPackage
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal Guid AgentId { get; private set; }
    internal uint AgentAccessEpoch { get; private set; }
    internal Guid ScriptEntryId { get; private set; }
    internal ulong ScriptRevision { get; private set; }
    internal ulong PackageRevision { get; private set; }
    internal ushort ContractVersion { get; private set; }
    internal uint RecipientAgentKeyVersion { get; private set; }
    internal byte[] RecipientAgentKeyFingerprint { get; private set; } = [];
    internal uint VaultSigningKeyVersion { get; private set; }
    internal byte[] VaultSigningKeyFingerprint { get; private set; } = [];
    internal byte[] ManifestDigest { get; private set; } = [];
    internal byte[] EncodedPackageCiphertext { get; private set; } = [];
    internal byte[] ProducerSignature { get; private set; } = [];

    private ScriptExecutionPackage() { }

    internal static ScriptExecutionPackage Create(
        Guid organizationId,
        Guid vaultId,
        Guid grantId,
        Guid agentId,
        uint agentAccessEpoch,
        Guid scriptEntryId,
        ulong scriptRevision,
        ulong packageRevision,
        ushort contractVersion,
        uint recipientAgentKeyVersion,
        byte[] recipientAgentKeyFingerprint,
        uint vaultSigningKeyVersion,
        byte[] vaultSigningKeyFingerprint,
        byte[] manifestDigest,
        byte[] encodedPackageCiphertext,
        byte[] producerSignature)
    {
        if (organizationId == Guid.Empty || vaultId == Guid.Empty || grantId == Guid.Empty
            || agentId == Guid.Empty || agentAccessEpoch == 0 || scriptEntryId == Guid.Empty
            || scriptRevision == 0 || packageRevision == 0 || contractVersion != 1
            || recipientAgentKeyVersion == 0
            || recipientAgentKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || vaultSigningKeyVersion == 0
            || vaultSigningKeyFingerprint.Length != VaultProtocol.FingerprintBytes
            || manifestDigest.Length != 32
            || encodedPackageCiphertext.Length is < 16 or > 2_097_152
            || producerSignature.Length != 64)
        {
            throw new DomainException("Script execution package is invalid.");
        }

        return new ScriptExecutionPackage
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            GrantId = grantId,
            AgentId = agentId,
            AgentAccessEpoch = agentAccessEpoch,
            ScriptEntryId = scriptEntryId,
            ScriptRevision = scriptRevision,
            PackageRevision = packageRevision,
            ContractVersion = contractVersion,
            RecipientAgentKeyVersion = recipientAgentKeyVersion,
            RecipientAgentKeyFingerprint = recipientAgentKeyFingerprint.ToArray(),
            VaultSigningKeyVersion = vaultSigningKeyVersion,
            VaultSigningKeyFingerprint = vaultSigningKeyFingerprint.ToArray(),
            ManifestDigest = manifestDigest.ToArray(),
            EncodedPackageCiphertext = encodedPackageCiphertext.ToArray(),
            ProducerSignature = producerSignature.ToArray(),
        };
    }

    internal void RefreshFrom(ScriptExecutionPackage replacement)
    {
        if (OrganizationId != replacement.OrganizationId || VaultId != replacement.VaultId
            || GrantId != replacement.GrantId || AgentId != replacement.AgentId
            || AgentAccessEpoch != replacement.AgentAccessEpoch
            || ScriptEntryId != replacement.ScriptEntryId
            || PackageRevision == ulong.MaxValue || replacement.PackageRevision != PackageRevision + 1)
        {
            throw new DomainException("Script execution package identity or revision is invalid.");
        }

        ScriptRevision = replacement.ScriptRevision;
        PackageRevision = replacement.PackageRevision;
        ContractVersion = replacement.ContractVersion;
        RecipientAgentKeyVersion = replacement.RecipientAgentKeyVersion;
        RecipientAgentKeyFingerprint = replacement.RecipientAgentKeyFingerprint.ToArray();
        VaultSigningKeyVersion = replacement.VaultSigningKeyVersion;
        VaultSigningKeyFingerprint = replacement.VaultSigningKeyFingerprint.ToArray();
        ManifestDigest = replacement.ManifestDigest.ToArray();
        EncodedPackageCiphertext = replacement.EncodedPackageCiphertext.ToArray();
        ProducerSignature = replacement.ProducerSignature.ToArray();
    }
}
