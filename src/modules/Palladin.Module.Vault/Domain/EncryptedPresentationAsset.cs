using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal enum PresentationAssetTarget : short
{
    Vault = 1,
    Entry = 2,
}

internal enum EncryptedPresentationAssetStatus : short
{
    PendingUpload = 1,
    Ready = 2,
}

internal sealed class EncryptedPresentationAsset
{
    internal const int DigestLength = 32;
    internal const int MaximumCiphertextBytes = 5 * 1024 * 1024;
    internal const string StoragePrefix = "encrypted-assets";

    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid Id { get; private set; }
    public PresentationAssetTarget Target { get; private set; }
    public Guid? EntryId { get; private set; }
    public Guid StorageId { get; private set; }
    public EncryptedPresentationAssetStatus Status { get; private set; }
    public string MediaType { get; private set; } = string.Empty;
    public int CiphertextLength { get; private set; }
    public byte[] CiphertextSha256 { get; private set; } = [];
    public Guid CreatedBy { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private EncryptedPresentationAsset() { }

    internal string StorageKey => $"{StoragePrefix}/{StorageId:N}";

    internal static EncryptedPresentationAsset Create(
        EntryScope? entryScope,
        Guid organizationId,
        Guid vaultId,
        Guid assetId,
        PresentationAssetTarget target,
        Guid storageId,
        string mediaType,
        int ciphertextLength,
        ReadOnlySpan<byte> ciphertextSha256,
        Guid createdBy,
        Instant createdAt)
    {
        if (organizationId == Guid.Empty || vaultId == Guid.Empty || assetId == Guid.Empty
            || storageId == Guid.Empty || createdBy == Guid.Empty)
        {
            throw new DomainException("Encrypted presentation asset scope is invalid.");
        }

        if (target == PresentationAssetTarget.Entry
            && (entryScope is null
                || entryScope.Value.OrganizationId != organizationId
                || entryScope.Value.VaultId != vaultId))
        {
            throw new DomainException("Encrypted Entry asset scope is invalid.");
        }

        if (target == PresentationAssetTarget.Vault && entryScope is not null)
        {
            throw new DomainException("Encrypted Vault assets cannot carry an Entry scope.");
        }

        if (!PresentationAssetMediaTypes.IsAllowed(mediaType)
            || ciphertextLength is < 1 or > MaximumCiphertextBytes
            || ciphertextSha256.Length != DigestLength)
        {
            throw new DomainException("Encrypted presentation asset metadata is invalid.");
        }

        return new EncryptedPresentationAsset
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            Id = assetId,
            Target = target,
            EntryId = entryScope?.EntryId,
            StorageId = storageId,
            Status = EncryptedPresentationAssetStatus.PendingUpload,
            MediaType = mediaType,
            CiphertextLength = ciphertextLength,
            CiphertextSha256 = ciphertextSha256.ToArray(),
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    internal void MarkReady(Instant completedAt)
    {
        Status = EncryptedPresentationAssetStatus.Ready;
        UpdatedAt = completedAt;
    }

    internal bool IsExactRetry(
        PresentationAssetTarget target,
        Guid? entryId,
        string mediaType,
        int ciphertextLength,
        ReadOnlySpan<byte> ciphertextSha256) =>
        Target == target
        && EntryId == entryId
        && string.Equals(MediaType, mediaType, StringComparison.Ordinal)
        && CiphertextLength == ciphertextLength
        && CiphertextSha256.AsSpan().SequenceEqual(ciphertextSha256);
}

internal static class PresentationAssetMediaTypes
{
    internal static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    internal static bool IsAllowed(string value) => Allowed.Contains(value);
}
