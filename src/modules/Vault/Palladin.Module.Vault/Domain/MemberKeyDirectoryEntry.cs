using NodaTime;
using Palladin.Core.Cryptography;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class MemberKeyDirectoryEntry
{
    internal Guid UserId { get; private set; }
    internal MemberRecipientKeyVersion KeyVersion { get; private set; }
    internal string PublicKeySuiteId { get; private set; } = string.Empty;
    internal byte[] Fingerprint { get; private set; } = [];
    internal byte[] PublicKey { get; private set; } = [];
    internal Instant UpdatedAt { get; private set; }

    private MemberKeyDirectoryEntry() { }

    internal static MemberKeyDirectoryEntry Create(
        Guid userId,
        MemberRecipientKeyVersion keyVersion,
        string publicKeySuiteId,
        byte[] fingerprint,
        byte[] publicKey,
        Instant updatedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException("Member key directory identity must be valid.");
        }

        ValidateKey(publicKeySuiteId, fingerprint, publicKey);
        return new MemberKeyDirectoryEntry
        {
            UserId = userId,
            KeyVersion = keyVersion,
            PublicKeySuiteId = publicKeySuiteId,
            Fingerprint = fingerprint.ToArray(),
            PublicKey = publicKey.ToArray(),
            UpdatedAt = updatedAt,
        };
    }

    internal void Replace(
        MemberRecipientKeyVersion keyVersion,
        string publicKeySuiteId,
        byte[] fingerprint,
        byte[] publicKey,
        Instant updatedAt)
    {
        ValidateKey(publicKeySuiteId, fingerprint, publicKey);
        if (keyVersion.Value < KeyVersion.Value)
        {
            return;
        }

        if (keyVersion == KeyVersion)
        {
            if (!string.Equals(publicKeySuiteId, PublicKeySuiteId, StringComparison.Ordinal)
                || !fingerprint.AsSpan().SequenceEqual(Fingerprint)
                || !publicKey.AsSpan().SequenceEqual(PublicKey))
            {
                throw new DomainException("A Member public key cannot change within one key version.");
            }

            if (updatedAt > UpdatedAt)
            {
                UpdatedAt = updatedAt;
            }

            return;
        }

        KeyVersion = keyVersion;
        PublicKeySuiteId = publicKeySuiteId;
        Fingerprint = fingerprint.ToArray();
        PublicKey = publicKey.ToArray();
        if (updatedAt > UpdatedAt)
        {
            UpdatedAt = updatedAt;
        }
    }

    internal bool Matches(MemberWrappedVaultKey wrappedVaultKey) =>
        wrappedVaultKey.RecipientKeyVersion == KeyVersion
        && string.Equals(wrappedVaultKey.WrapperSuiteId, PublicKeySuiteId, StringComparison.Ordinal)
        && wrappedVaultKey.RecipientKeyFingerprint.AsSpan().SequenceEqual(Fingerprint);

    private static void ValidateKey(string publicKeySuiteId, byte[] fingerprint, byte[] publicKey)
    {
        KeyWrapperSuites.ResolveForOpen(publicKeySuiteId).ValidatePublicKey(publicKey);
        if (fingerprint.Length != VaultProtocol.FingerprintBytes)
        {
            throw new DomainException("Member key directory fingerprints must use the frozen protocol length.");
        }
    }
}
