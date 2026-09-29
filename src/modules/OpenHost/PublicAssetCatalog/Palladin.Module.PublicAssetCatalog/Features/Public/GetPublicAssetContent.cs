using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;

namespace Palladin.Module.PublicAssetCatalog.Features;

[PublicAPI]
public sealed record GetPublicAssetContentRequest(Guid AssetId, int Revision);

[UsedImplicitly]
internal sealed class GetPublicAssetContentValidator : Validator<GetPublicAssetContentRequest>
{
    public GetPublicAssetContentValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty();
        RuleFor(x => x.Revision).GreaterThan(0);
    }
}

[PublicAPI]
internal sealed class GetPublicAssetContentEndpoint(
    PublicAssetCatalogDomainReadContext db,
    IPublicAssetStorage storage) : Endpoint<GetPublicAssetContentRequest>
{
    public override void Configure()
    {
        Get("api/public-assets/{assetId:guid}/revisions/{revision:int}/content");
        // These sanitized catalog images are already public in object storage.
        AllowAnonymous();
        Tags("Public Assets/Content");
    }

    public override async Task HandleAsync(GetPublicAssetContentRequest req, CancellationToken ct)
    {
        var revision = await db.Assets
            .Where(asset => asset.Id == req.AssetId && asset.Status == PublicAssetStatus.Ready)
            .SelectMany(asset => asset.Revisions)
            .Where(item => item.Revision == req.Revision)
            .Select(item => new { item.StorageKey, item.MediaType })
            .SingleOrDefaultAsync(ct);
        if (revision is null || revision.MediaType != "image/png")
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var content = await storage.OpenImmutableAsync(revision.StorageKey, revision.MediaType, ct);
        if (content?.ExistingContent is not { } bytes)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
        await Send.BytesAsync(bytes, contentType: "image/png", cancellation: ct);
    }
}
