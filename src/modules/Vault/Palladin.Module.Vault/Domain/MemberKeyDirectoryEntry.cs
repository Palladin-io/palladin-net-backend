using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class MemberKeyDirectoryEntry
{
    internal Guid UserId { get; private set; }
    internal MemberRecipientKeyVersion KeyVersion { get; private set; }
    internal byte[] Fingerprint { get; private set; } = [];
    internal byte[] PublicKey { get; private set; } = [];
    internal Instant UpdatedAt { get; private set; }

    private MemberKeyDirectoryEntry() { }

    internal static MemberKeyDirectoryEntry Create(
        Guid userId,
        MemberRecipientKeyVersion keyVersion,
        byte[] fingerprint,
        byte[] publicKey,
        Instant updatedAt)
    {
        if (userId == Guid.Empty || fingerprint.Length != VaultProtocol.FingerprintBytes
            || publicKey.Length != VaultProtocol.FingerprintBytes)
        {
            throw new DomainException("Member key directory identity and fingerprint must be valid.");
        }

        return new MemberKeyDirectoryEntry
        {
            UserId = userId,
            KeyVersion = keyVersion,
            Fingerprint = fingerprint.ToArray(),
            PublicKey = publicKey.ToArray(),
            UpdatedAt = updatedAt,
        };
    }

    internal void Replace(
        MemberRecipientKeyVersion keyVersion,
        byte[] fingerprint,
        byte[] publicKey,
        Instant updatedAt)
    {
        if (fingerprint.Length != VaultProtocol.FingerprintBytes
            || publicKey.Length != VaultProtocol.FingerprintBytes)
        {
            throw new DomainException("Member key directory fingerprints must use the frozen protocol length.");
        }

        if (keyVersion.Value < KeyVersion.Value)
        {
            return;
        }

        if (keyVersion == KeyVersion)
        {
            if (!fingerprint.AsSpan().SequenceEqual(Fingerprint)
                || !publicKey.AsSpan().SequenceEqual(PublicKey))
            {
                throw new DomainException("A Member public-key fingerprint cannot change within one key version.");
            }

            if (updatedAt > UpdatedAt)
            {
                UpdatedAt = updatedAt;
            }

            return;
        }

        KeyVersion = keyVersion;
        Fingerprint = fingerprint.ToArray();
        PublicKey = publicKey.ToArray();
        if (updatedAt > UpdatedAt)
        {
            UpdatedAt = updatedAt;
        }
    }

    internal bool Matches(MemberWrappedVaultKey wrappedVaultKey) =>
        wrappedVaultKey.RecipientKeyVersion == KeyVersion
        && wrappedVaultKey.RecipientKeyFingerprint.AsSpan().SequenceEqual(Fingerprint);
}
