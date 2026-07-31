using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;
using Palladin.Module.PublicAssetCatalog.Contracts.Commands;
using MassTransit;

namespace Palladin.Module.PublicAssetCatalog.Features;

[PublicAPI] public sealed record PublicAssetContract(Guid Id, string Type, string Name, string Url, int Revision, IReadOnlyList<string>? Aliases = null);
[PublicAPI] public sealed record SearchPublicAssetsRequest(string Type, string Q, int? Limit);
[PublicAPI] public sealed record SearchPublicAssetsResponse(IReadOnlyList<PublicAssetContract> Items);
[PublicAPI] public sealed record ResolvePublicAssetsRequest(string Type, IReadOnlyList<string> Hostnames, bool? AcquireMissing = null);
[PublicAPI] public sealed record ResolvedPublicAssetContract(string Hostname, PublicAssetContract? Asset);
[PublicAPI] public sealed record ResolvePublicAssetsResponse(IReadOnlyList<ResolvedPublicAssetContract> Items);
[PublicAPI] public sealed record GetPublicAssetRequest(Guid AssetId, int? V);
[PublicAPI] public sealed record GetPublicAssetsByIdsRequest(IReadOnlyList<Guid> AssetIds);

internal static class PublicAssetContracts
{
    internal static PublicAssetContract Map(PublicAsset asset, IPublicAssetStorage storage)
    {
        var revision = asset.Revisions.Single(x => x.Revision == asset.CurrentRevision);
        return new(asset.Id, asset.Type == PublicAssetType.AgentIcon ? "agentIcon" : "websiteIcon", asset.Name, storage.GetDeliveryUrl(revision.StorageKey), revision.Revision, asset.Aliases.Select(x => x.Value).ToArray());
    }
    internal static bool TryHostname(string input, out string hostname)
    {
        hostname = input.Trim().TrimEnd('.').ToLowerInvariant();
        if (hostname.Length is 0 or > 253 || Uri.CheckHostName(hostname) != UriHostNameType.Dns || hostname == "localhost" || hostname.EndsWith(".localhost") || hostname.EndsWith(".local") || hostname.EndsWith(".internal")) return false;
        return hostname.Contains('.');
    }
    internal static IReadOnlyDictionary<string, PublicAsset> BuildHostnameMap(IEnumerable<PublicAsset> assets) =>
        assets.SelectMany(asset => asset.Aliases
                .Where(alias => alias.Kind == PublicAssetAliasKind.Hostname)
                .Select(alias => (alias.Value, Asset: asset)))
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            // Historical retries may have produced duplicate catalog rows.
            // Resolve remains total and deterministic instead of failing the
            // complete client batch because one hostname occurs twice.
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Asset.CurrentRevision)
                    .ThenBy(item => item.Asset.Id)
                    .First().Asset,
                StringComparer.Ordinal);
}
[UsedImplicitly] internal sealed class GetPublicAssetsByIdsValidator : Validator<GetPublicAssetsByIdsRequest> { public GetPublicAssetsByIdsValidator() { RuleFor(x => x.AssetIds).NotEmpty().Must(x => x.Count <= 200 && x.Distinct().Count() == x.Count); RuleForEach(x => x.AssetIds).NotEmpty(); } }
[PublicAPI]
internal sealed class GetPublicAssetsByIdsEndpoint(PublicAssetCatalogDomainReadContext db, IPublicAssetStorage storage) : Endpoint<GetPublicAssetsByIdsRequest, SearchPublicAssetsResponse>
{
    public override void Configure() { Post("api/public-assets/by-ids"); AllowAnonymous(); Tags("Public Assets"); }
    public override async Task HandleAsync(GetPublicAssetsByIdsRequest req, CancellationToken ct)
    {
        var assets = await db.Assets.Include(x => x.Aliases).Include(x => x.Revisions).Where(x => req.AssetIds.Contains(x.Id) && x.Status == PublicAssetStatus.Ready).ToListAsync(ct);
        var map = assets.ToDictionary(x => x.Id); await Send.OkAsync(new(req.AssetIds.Where(map.ContainsKey).Select(x => PublicAssetContracts.Map(map[x], storage)).ToArray()), ct);
    }
}
[UsedImplicitly] internal sealed class SearchPublicAssetsValidator : Validator<SearchPublicAssetsRequest> { public SearchPublicAssetsValidator() { RuleFor(x => x.Type).Equal("websiteIcon"); RuleFor(x => x.Q).NotEmpty().MaximumLength(200); RuleFor(x => x.Limit).InclusiveBetween(1, 40).When(x => x.Limit.HasValue); } }
[PublicAPI]
internal sealed class SearchPublicAssetsEndpoint(PublicAssetCatalogDomainReadContext db, IPublicAssetStorage storage) : Endpoint<SearchPublicAssetsRequest, SearchPublicAssetsResponse>
{
    public override void Configure() { Get("api/public-assets/search"); AllowAnonymous(); Tags("Public Assets"); }
    public override async Task HandleAsync(SearchPublicAssetsRequest req, CancellationToken ct)
    {
        var q = req.Q.Trim().ToLowerInvariant(); var limit = req.Limit ?? 40;
        var assets = await db.Assets.Include(x => x.Aliases).Include(x => x.Revisions).Where(x => x.Type == PublicAssetType.WebsiteIcon && x.Status == PublicAssetStatus.Ready && (x.Name.ToLower().Contains(q) || x.Aliases.Any(a => a.Value.Contains(q)))).OrderBy(x => x.Name).Take(limit).ToListAsync(ct);
        await Send.OkAsync(new(assets.Select(x => PublicAssetContracts.Map(x, storage)).ToArray()), ct);
    }
}
[UsedImplicitly] internal sealed class ResolvePublicAssetsValidator : Validator<ResolvePublicAssetsRequest> { public ResolvePublicAssetsValidator() { RuleFor(x => x.Type).Equal("websiteIcon"); RuleFor(x => x.Hostnames).NotEmpty().Must(x => x.Count <= 500); RuleForEach(x => x.Hostnames).Must(x => PublicAssetContracts.TryHostname(x, out _)); } }
[PublicAPI]
internal sealed class ResolvePublicAssetsEndpoint(PublicAssetCatalogDomainReadContext db, IPublicAssetStorage storage, IPublishEndpoint publisher) : Endpoint<ResolvePublicAssetsRequest, ResolvePublicAssetsResponse>
{
    public override void Configure() { Post("api/public-assets/resolve"); AllowAnonymous(); Tags("Public Assets"); }
    public override async Task HandleAsync(ResolvePublicAssetsRequest req, CancellationToken ct)
    {
        var hosts = req.Hostnames.Select(x => { PublicAssetContracts.TryHostname(x, out var h); return h; }).Distinct().ToArray();
        var knownHosts = await db.Assets.Where(x => x.Status == PublicAssetStatus.Ready).SelectMany(x => x.Aliases).Where(x => x.Kind == PublicAssetAliasKind.Hostname && hosts.Contains(x.Value)).Select(x => x.Value).ToArrayAsync(ct);
        // RabbitMQ is the durable backpressure boundary. Imports may contain
        // any number of client-side pages; accepted commands survive process
        // restarts and are never dropped because an in-memory channel is full.
        if (req.AcquireMissing is not false)
            foreach (var hostname in hosts.Except(knownHosts))
                await publisher.Publish(new AcquireWebsiteIconCommand(hostname), ct);
        var assets = await db.Assets.Include(x => x.Aliases).Include(x => x.Revisions).Where(x => x.Status == PublicAssetStatus.Ready && x.Aliases.Any(a => a.Kind == PublicAssetAliasKind.Hostname && hosts.Contains(a.Value))).ToListAsync(ct);
        var map = PublicAssetContracts.BuildHostnameMap(assets);
        await Send.OkAsync(new(hosts.Select(h => new ResolvedPublicAssetContract(h, map.TryGetValue(h, out var a) ? PublicAssetContracts.Map(a, storage) : null)).ToArray()), ct);
    }
}
[PublicAPI]
internal sealed class GetPublicAssetEndpoint(PublicAssetCatalogDomainReadContext db, IPublicAssetStorage storage) : Endpoint<GetPublicAssetRequest, PublicAssetContract>
{
    public override void Configure() { Get("api/public-assets/{assetId:guid}"); AllowAnonymous(); Tags("Public Assets"); }
    public override async Task HandleAsync(GetPublicAssetRequest req, CancellationToken ct)
    {
        var asset = await db.Assets.Include(x => x.Aliases).Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == req.AssetId && x.Status == PublicAssetStatus.Ready, ct);
        if (asset is null || req.V is not null && asset.CurrentRevision != req.V) { await Send.NotFoundAsync(ct); return; }
        await Send.OkAsync(PublicAssetContracts.Map(asset, storage), ct);
    }
}
