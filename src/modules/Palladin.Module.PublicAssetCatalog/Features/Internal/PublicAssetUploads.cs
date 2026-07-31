using System.Security.Cryptography;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Infrastructure;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Palladin.Module.PublicAssetCatalog.Features;

[PublicAPI] public sealed record CreatePublicAssetUploadRequest(string Type, string Name, IReadOnlyList<string> Hostnames, IReadOnlyList<string>? Aliases, string MediaType, long ByteLength, string Sha256, Guid? OrganizationId = null, Guid? OwnerId = null);
[PublicAPI] public sealed record CreatePublicAssetUploadResponse(Guid AssetId, Guid UploadSessionId, string UploadUrl, Instant ExpiresAt, long MaximumBytes);
[PublicAPI] public sealed record CompletePublicAssetUploadRequest(Guid UploadSessionId, Guid? OrganizationId = null, Guid? OwnerId = null);

[UsedImplicitly]
internal sealed class CreatePublicAssetUploadValidator : Validator<CreatePublicAssetUploadRequest>
{
    public CreatePublicAssetUploadValidator() { RuleFor(x => x.Type).Must(x => x is "websiteIcon" or "agentIcon"); RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.Hostnames).Must(x => x.Count <= 20); RuleForEach(x => x.Hostnames).Must(x => PublicAssetContracts.TryHostname(x, out _)); RuleFor(x => x.OrganizationId).NotEmpty().When(x => x.Type == "agentIcon"); RuleFor(x => x.OwnerId).NotEmpty().When(x => x.Type == "agentIcon"); RuleFor(x => x.Hostnames).Empty().When(x => x.Type == "agentIcon"); RuleFor(x => x.Aliases).Must(x => x is null || x.Count <= 30); RuleForEach(x => x.Aliases!).NotEmpty().MaximumLength(200); RuleFor(x => x.MediaType).Must(x => x is "image/png" or "image/jpeg" or "image/webp"); RuleFor(x => x.ByteLength).InclusiveBetween(1, 1024 * 1024); RuleFor(x => x.Sha256).Matches("^[a-f0-9]{64}$"); }
}
[PublicAPI]
internal sealed class CreatePublicAssetUploadEndpoint(PublicAssetCatalogDomainWriteContext db, IGuidProvider ids, IClock clock, IPublicAssetStorage storage, IOptions<PublicAssetStorageOptions> options) : Endpoint<CreatePublicAssetUploadRequest, CreatePublicAssetUploadResponse>
{
    public override void Configure() { Post("api/public-assets/uploads"); AuthSchemes(PublicAssetServiceAuthentication.Scheme); Roles(PublicAssetServiceAuthentication.Role); Tags("Public Assets/Internal"); }
    public override async Task HandleAsync(CreatePublicAssetUploadRequest req, CancellationToken ct)
    {
        var serviceSubject = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value; if (serviceSubject is null) { await Send.UnauthorizedAsync(ct); return; }
        var now = clock.GetCurrentInstant(); var assetId = ids.Generate(); var sessionId = ids.Generate();
        var aliases = req.Hostnames.Select(x => { PublicAssetContracts.TryHostname(x, out var h); return (h, PublicAssetAliasKind.Hostname); }).Concat((req.Aliases ?? []).Select(x => (x.Trim().ToLowerInvariant(), PublicAssetAliasKind.Name)));
        var hostnames = req.Hostnames.Select(x => { PublicAssetContracts.TryHostname(x, out var h); return h; }).ToArray();
        if (await db.Assets.AnyAsync(x => x.Aliases.Any(a => a.Kind == PublicAssetAliasKind.Hostname && hostnames.Contains(a.Value)), ct)) { AddError("A hostname is already assigned to another catalog asset."); await Send.ErrorsAsync(cancellation: ct); return; }
        var asset = req.Type == "agentIcon" ? await db.Assets.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.OrganizationId == req.OrganizationId && x.Type == PublicAssetType.AgentIcon && x.OwnerId == req.OwnerId, ct) : null; var assetCreated = asset is null;
        asset ??= req.Type == "agentIcon" ? PublicAsset.CreateAgentIcon(assetId, req.OrganizationId!.Value, req.OwnerId!.Value, req.Name.Trim()) : PublicAsset.Create(assetId, req.Name.Trim(), aliases); assetId = asset.Id; var session = PublicAssetUploadSession.CreateForService(sessionId, assetId, serviceSubject, req.Sha256, req.MediaType, req.ByteLength, now + options.Value.UploadExpiry);
        if (assetCreated) db.Add(asset); db.Add(session); await db.CommitAsync(ct); var url = await storage.CreateUploadUrlAsync(session.StagingKey, req.MediaType, session.ExpiresAt, ct);
        await Send.OkAsync(new(assetId, sessionId, url, session.ExpiresAt, options.Value.MaximumBytes), ct);
    }
}
[PublicAPI]
internal sealed class CompletePublicAssetUploadEndpoint(PublicAssetCatalogDomainWriteContext db, IClock clock, IPublicAssetStorage storage, IOptions<PublicAssetStorageOptions> options) : Endpoint<CompletePublicAssetUploadRequest, PublicAssetContract>
{
    public override void Configure() { Post("api/public-assets/uploads/{uploadSessionId:guid}/complete"); AuthSchemes(PublicAssetServiceAuthentication.Scheme); Roles(PublicAssetServiceAuthentication.Role); Tags("Public Assets/Internal"); }
    public override async Task HandleAsync(CompletePublicAssetUploadRequest req, CancellationToken ct)
    {
        var serviceSubject = User.FindFirst("sub")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value; if (serviceSubject is null) { await Send.UnauthorizedAsync(ct); return; }
        var session = await db.UploadSessions.SingleOrDefaultAsync(x => x.Id == req.UploadSessionId && x.ServiceSubject == serviceSubject, ct); if (session is null) { await Send.NotFoundAsync(ct); return; }
        var asset = await db.Assets.Include(x => x.Aliases).Include(x => x.Revisions).SingleAsync(x => x.Id == session.AssetId, ct);
        if (asset.Type == PublicAssetType.AgentIcon && (asset.OrganizationId != req.OrganizationId || asset.OwnerId != req.OwnerId)) { await Send.NotFoundAsync(ct); return; }
        if (session.CompletedAt is not null) { await Send.OkAsync(PublicAssetContracts.Map(asset, storage), ct); return; }
        if (session.IsExpired(clock.GetCurrentInstant())) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Upload session expired."); await Send.ErrorsAsync(cancellation: ct); return; }
        var staged = await storage.OpenStagedAsync(session.StagingKey, ct); if (staged is null) { AddError("Uploaded object was not found."); await Send.ErrorsAsync(cancellation: ct); return; }
        await using var source = staged.Content;
        if (staged.Length != session.ExpectedByteLength || staged.Length > options.Value.MaximumBytes || staged.MediaType != session.ExpectedMediaType) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Uploaded object metadata does not match the session."); await Send.ErrorsAsync(cancellation: ct); return; }
        await using var bytes = new MemoryStream(); await source.CopyToAsync(bytes, ct); var digest = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant(); if (digest != session.ExpectedDigest) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Uploaded object digest does not match the session."); await Send.ErrorsAsync(cancellation: ct); return; }
        bytes.Position = 0; ImageInfo info; try { info = await Image.IdentifyAsync(bytes, ct); } catch (UnknownImageFormatException) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Uploaded object is not a supported image."); await Send.ErrorsAsync(cancellation: ct); return; }
        if (info.Metadata.DecodedImageFormat?.DefaultMimeType != session.ExpectedMediaType) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Decoded image format does not match the declared media type."); await Send.ErrorsAsync(cancellation: ct); return; }
        if (info.Width is < 1 or > 2048 || info.Height is < 1 or > 2048 || (long)info.Width * info.Height > 4_000_000) { await storage.DeleteStagedAsync(session.StagingKey, ct); AddError("Uploaded image dimensions exceed safe limits."); await Send.ErrorsAsync(cancellation: ct); return; }
        bytes.Position = 0; var image = await Image.LoadAsync(bytes, ct);
        using (image) { if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048 || (long)image.Width * image.Height > 4_000_000) { AddError("Uploaded image dimensions exceed safe limits."); await Send.ErrorsAsync(cancellation: ct); return; } await using var sanitized = new MemoryStream(); await image.SaveAsync(sanitized, new PngEncoder(), ct); sanitized.Position = 0; var publishedDigest = Convert.ToHexString(SHA256.HashData(sanitized.ToArray())).ToLowerInvariant(); sanitized.Position = 0; var nextRevision = (asset.CurrentRevision ?? 0) + 1; var key = $"published/{(asset.Type == PublicAssetType.AgentIcon ? "agent-icon" : "website-icon")}/{publishedDigest[..2]}/{publishedDigest}/{nextRevision}.png"; await storage.PublishAsync(session.StagingKey, sanitized, key, "image/png", ct); asset.Publish(publishedDigest, "image/png", sanitized.Length, image.Width, image.Height, key, clock.GetCurrentInstant()); session.Complete(clock.GetCurrentInstant()); await db.CommitAsync(ct); await Send.OkAsync(PublicAssetContracts.Map(asset, storage), ct); }
    }
}
