using NodaTime;

namespace Palladin.Module.PublicAssetCatalog.Domain;

internal enum PublicAssetStatus { Pending = 1, Ready = 2, Deleted = 3, Failed = 4 }
internal enum PublicAssetType { WebsiteIcon = 1, AgentIcon = 2 }
internal enum PublicAssetAliasKind { Hostname = 1, Name = 2, Slug = 3, Tag = 4 }

internal sealed class PublicAsset
{
    public Guid Id { get; private set; }
    public PublicAssetType Type { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid? OrganizationId { get; private set; }
    public Guid? OwnerId { get; private set; }
    public PublicAssetStatus Status { get; private set; }
    public int? CurrentRevision { get; private set; }
    public Instant? AcquisitionScheduledAt { get; private set; }
    public List<PublicAssetAlias> Aliases { get; private set; } = [];
    public List<PublicAssetRevision> Revisions { get; private set; } = [];
    private PublicAsset() { }
    internal static PublicAsset Create(Guid id, string name, IEnumerable<(string Value, PublicAssetAliasKind Kind)> aliases)
    {
        var asset = new PublicAsset { Id = id, Type = PublicAssetType.WebsiteIcon, Name = name, Status = PublicAssetStatus.Pending };
        asset.Aliases.AddRange(aliases.Distinct().Select(x => PublicAssetAlias.Create(id, x.Value, x.Kind)));
        return asset;
    }
    internal static PublicAsset CreateAgentIcon(Guid id, Guid organizationId, Guid ownerId, string name) =>
        new() { Id = id, Type = PublicAssetType.AgentIcon, Name = name, OrganizationId = organizationId, OwnerId = ownerId, Status = PublicAssetStatus.Pending };
    internal void AbandonPendingWebsiteUpload()
    {
        if (Type != PublicAssetType.WebsiteIcon || Status != PublicAssetStatus.Pending)
            throw new InvalidOperationException("Only a pending website-icon upload can be abandoned.");
        Aliases.Clear();
        Status = PublicAssetStatus.Deleted;
    }
    internal void FailWebsiteIconAcquisition()
    {
        if (Type != PublicAssetType.WebsiteIcon || Status != PublicAssetStatus.Pending)
            throw new InvalidOperationException("Only a pending website-icon acquisition can fail.");
        Status = PublicAssetStatus.Failed;
    }
    internal bool TryScheduleWebsiteIconAcquisition(Instant now, Duration retryAfter)
    {
        if (Type != PublicAssetType.WebsiteIcon || Status != PublicAssetStatus.Pending)
            return false;
        if (AcquisitionScheduledAt is { } scheduledAt && now - scheduledAt < retryAfter)
            return false;
        AcquisitionScheduledAt = now;
        return true;
    }
    internal void Publish(string digest, string mediaType, long byteLength, int width, int height, string storageKey, Instant now)
    {
        if (Status == PublicAssetStatus.Ready && Type != PublicAssetType.AgentIcon) throw new InvalidOperationException("A published asset is immutable.");
        var revision = (CurrentRevision ?? 0) + 1;
        Revisions.Add(PublicAssetRevision.Create(Id, revision, digest, mediaType, byteLength, width, height, storageKey, now));
        CurrentRevision = revision;
        Status = PublicAssetStatus.Ready;
    }
}

internal sealed class PublicAssetAlias
{
    public Guid AssetId { get; private set; }
    public string Value { get; private set; } = string.Empty;
    public PublicAssetAliasKind Kind { get; private set; }
    private PublicAssetAlias() { }
    internal static PublicAssetAlias Create(Guid assetId, string value, PublicAssetAliasKind kind) => new() { AssetId = assetId, Value = value, Kind = kind };
}

internal sealed class PublicAssetRevision
{
    public Guid AssetId { get; private set; }
    public int Revision { get; private set; }
    public string Digest { get; private set; } = string.Empty;
    public string MediaType { get; private set; } = string.Empty;
    public long ByteLength { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public Instant PublishedAt { get; private set; }
    private PublicAssetRevision() { }
    internal static PublicAssetRevision Create(Guid id, int revision, string digest, string mediaType, long length, int width, int height, string key, Instant now) =>
        new() { AssetId = id, Revision = revision, Digest = digest, MediaType = mediaType, ByteLength = length, Width = width, Height = height, StorageKey = key, PublishedAt = now };
}

internal sealed class PublicAssetUploadSession
{
    public Guid Id { get; private set; }
    public Guid AssetId { get; private set; }
    public Guid UploaderId { get; private set; }
    public string ServiceSubject { get; private set; } = string.Empty;
    public string ExpectedDigest { get; private set; } = string.Empty;
    public string ExpectedMediaType { get; private set; } = string.Empty;
    public long ExpectedByteLength { get; private set; }
    public string StagingKey { get; private set; } = string.Empty;
    public Instant ExpiresAt { get; private set; }
    public Instant? CompletedAt { get; private set; }
    private PublicAssetUploadSession() { }
    internal static PublicAssetUploadSession Create(Guid id, Guid assetId, Guid uploader, string digest, string mediaType, long length, Instant expires) =>
        new() { Id = id, AssetId = assetId, UploaderId = uploader, ExpectedDigest = digest, ExpectedMediaType = mediaType, ExpectedByteLength = length, StagingKey = $"staging/{id:N}", ExpiresAt = expires };
    internal static PublicAssetUploadSession CreateForService(Guid id, Guid assetId, string serviceSubject, string digest, string mediaType, long length, Instant expires) =>
        new() { Id = id, AssetId = assetId, ServiceSubject = serviceSubject, ExpectedDigest = digest, ExpectedMediaType = mediaType, ExpectedByteLength = length, StagingKey = $"staging/{id:N}", ExpiresAt = expires };
    internal void Complete(Instant now) { if (CompletedAt is not null) return; if (now >= ExpiresAt) throw new InvalidOperationException("Upload session expired."); CompletedAt = now; }
    internal bool IsExpired(Instant now) => now >= ExpiresAt;
}
