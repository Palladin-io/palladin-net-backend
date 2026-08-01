using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal readonly record struct CryptoSuiteId
{
    internal const string XChaCha20Poly1305V1 = "palladin-vault-xchacha-v1";

    internal string Value { get; }

    internal CryptoSuiteId(string value)
    {
        if (!string.Equals(value, XChaCha20Poly1305V1, StringComparison.Ordinal))
        {
            throw new DomainException("The crypto suite is not registered by Vault protocol 2.");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

internal enum EnvelopePurpose : ushort
{
    MemberVaultMetadata = 1,
    VaultDiscoveryKey = 2,
    VaultAgentMessagePrivateKey = 3,
    VaultManifestSigningPrivateKey = 4,
    MemberIndex = 5,
    MemberSecret = 6,
    AgentDiscovery = 7,
    EntryDekByVaultKey = 8,
    EncryptedReason = 9,
    GrantPayload = 10,
}

[Flags]
internal enum EnvelopeScopeFields : ushort
{
    Organization = 1 << 0,
    Vault = 1 << 1,
    Entry = 1 << 2,
    GrantOrRequest = 1 << 3,
    Agent = 1 << 4,
    Member = 1 << 5,
}

internal sealed record EnvelopeScope(
    Guid OrganizationId,
    Guid VaultId,
    Guid? EntryId = null,
    Guid? GrantOrRequestId = null,
    Guid? AgentId = null,
    Guid? MemberId = null)
{
    internal EnvelopeScopeFields Fields =>
        EnvelopeScopeFields.Organization
        | EnvelopeScopeFields.Vault
        | (EntryId.HasValue ? EnvelopeScopeFields.Entry : 0)
        | (GrantOrRequestId.HasValue ? EnvelopeScopeFields.GrantOrRequest : 0)
        | (AgentId.HasValue ? EnvelopeScopeFields.Agent : 0)
        | (MemberId.HasValue ? EnvelopeScopeFields.Member : 0);

    internal void Validate(EnvelopeScopeFields expected)
    {
        if (Fields != expected || OrganizationId == Guid.Empty || VaultId == Guid.Empty
            || EntryId == Guid.Empty || GrantOrRequestId == Guid.Empty || AgentId == Guid.Empty
            || MemberId == Guid.Empty)
        {
            throw new DomainException("Envelope scope does not match its registered purpose.");
        }
    }
}

internal abstract record EnvelopeBinding;
internal sealed record EmptyEnvelopeBinding : EnvelopeBinding;
internal sealed record MemberSecretEnvelopeBinding(ushort Operation) : EnvelopeBinding;
internal sealed record VaultKeyEnvelopeBinding(uint WrappingVaultKeyVersion) : EnvelopeBinding;
internal sealed record ReasonEnvelopeBinding(
    string WrapperSuiteId,
    uint RecipientKeyVersion,
    byte[] RecipientKeyFingerprint,
    ushort RequestedMethods) : EnvelopeBinding;
internal sealed record GrantEnvelopeBinding(
    ulong EntryRevision,
    string WrapperSuiteId,
    uint RecipientKeyVersion,
    byte[] RecipientKeyFingerprint,
    ushort ApprovedMethods,
    ushort DeliveryPolicy,
    byte[] FieldSetCommitment,
    long? ExpiresAtUnixSeconds,
    uint? ExpiresAtNanoseconds,
    uint? RemainingUses) : EnvelopeBinding;

internal sealed record EnvelopeDescriptor(
    ushort ProtocolVersion,
    CryptoSuiteId CryptoSuiteId,
    EnvelopePurpose Purpose,
    EnvelopeScope Scope,
    ulong ResourceRevision,
    uint KeyVersion,
    uint? MemberKeyGeneration,
    EnvelopeBinding Binding);

internal sealed record EncodedSuitePayload
{
    internal byte[] Bytes { get; }

    internal EncodedSuitePayload(byte[] bytes)
    {
        Bytes = bytes.ToArray();
    }
}

internal static class SuitePayload
{
    internal static byte[] Encode(byte[] nonce, byte[] ciphertext)
    {
        if (nonce.Length != VaultProtocol.NonceBytes)
            throw new DomainException("XChaCha20-Poly1305 nonce must contain exactly 24 bytes.");
        var payload = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, nonce.Length);
        return payload;
    }

    internal static byte[] Nonce(byte[] payload) => payload[..VaultProtocol.NonceBytes];
    internal static byte[] Ciphertext(byte[] payload) => payload[VaultProtocol.NonceBytes..];
}

internal sealed record ValidatedVaultPublicKey(
    VaultPublicKeyKind Kind,
    uint Version,
    byte[] PublicKey,
    byte[] Fingerprint);

internal enum VaultPublicKeyKind : ushort
{
    AgentMessageX25519 = 1,
    ManifestSigningEd25519 = 2,
}
