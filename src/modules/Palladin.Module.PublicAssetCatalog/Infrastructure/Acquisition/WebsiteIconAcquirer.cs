using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NodaTime;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Features;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

internal interface IWebsiteIconAcquirer
{
    Task AcquireAsync(Guid assetId, string hostname, CancellationToken ct);
}

/// <summary>Best-effort public favicon acquisition with DNS pinning and redirect revalidation.</summary>
internal sealed class WebsiteIconAcquirer(
    PublicAssetCatalogDomainWriteContext db,
    IPublicAssetStorage storage,
    IClock clock) : IWebsiteIconAcquirer
{
    private const int MaximumDownloadBytes = 1024 * 1024;
    private const int MaximumRedirects = 3;
    private const int MaximumDiscoveredCandidates = 16;
    private static readonly string[] ConventionalPaths =
    [
        "/favicon.ico", "/favicon.png", "/apple-touch-icon.png",
        "/apple-touch-icon-precomposed.png", "/android-chrome-512x512.png",
        "/android-chrome-192x192.png",
    ];

    public async Task AcquireAsync(Guid assetId, string hostname, CancellationToken ct)
    {
        if (!PublicNetworkPolicy.IsValidHostname(hostname)) return;
        var asset = await db.Assets
            .Include(x => x.Aliases)
            .Include(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == assetId
                && x.Type == PublicAssetType.WebsiteIcon
                && x.Aliases.Any(a => a.Kind == PublicAssetAliasKind.Hostname && a.Value == hostname), ct);
        if (asset is null || asset.Status != PublicAssetStatus.Pending) return;

        var origin = new Uri($"https://{hostname}/");
        var fallbackHostname = ParentHostname(hostname);
        var candidates = new List<IconCandidate>();
        var page = await TryDownloadCandidateAsync(origin, ct);
        if (page is not null)
        {
            var html = Encoding.UTF8.GetString(page);
            candidates.AddRange(DiscoverHtmlIconCandidates(origin, html));
            var manifest = DiscoverManifestUri(origin, html);
            if (manifest is not null)
            {
                var manifestBytes = await TryDownloadCandidateAsync(manifest, ct);
                if (manifestBytes is not null)
                    candidates.AddRange(DiscoverManifestIconCandidates(manifest, manifestBytes));
            }
        }
        candidates.AddRange(ConventionalCandidates(origin, 2_000));
        if (fallbackHostname is not null)
            candidates.AddRange(ConventionalCandidates(new Uri($"https://{fallbackHostname}/"), 1_400));
        // Some large providers block origin favicon requests even for a
        // browser-like UA. Fetch through Google's public favicon endpoint as a
        // final source; the returned bytes are still size-limited, decoded and
        // re-encoded before entering our catalog.
        // Keep the exact-host public fallback ahead of low-confidence guessed
        // paths. This prevents six sequential misses from starving the queue
        // for applications whose icon exists only for a subdomain.
        candidates.Add(new(new Uri($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(hostname)}&sz=128"), 2_100));
        if (fallbackHostname is not null)
            // Login/application subdomains frequently have no independent
            // favicon record while the registrable parent does. Keep this in
            // the first wave instead of making it wait behind six guessed
            // paths that may each consume the complete timeout budget.
            candidates.Add(new(new Uri($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(fallbackHostname)}&sz=128"), 2_050));

        Image? image = null;
        var rankedCandidates = candidates
            .Where(x => x.Uri.Scheme == Uri.UriSchemeHttps && !x.Uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Uri).Select(x => x.OrderByDescending(y => y.Score).First())
            .OrderByDescending(x => x.Score).Take(MaximumDiscoveredCandidates).ToArray();
        // Independent candidates are fetched in small ranked waves. A site
        // with six missing conventional paths now costs one timeout wave, not
        // six sequential timeouts, while the cap keeps outbound work bounded.
        foreach (var wave in rankedCandidates.Chunk(4))
        {
            var downloaded = await Task.WhenAll(wave.Select(async candidate =>
                (candidate, bytes: await TryDownloadCandidateAsync(candidate.Uri, ct))));
            foreach (var result in downloaded.OrderByDescending(x => x.candidate.Score))
            {
                if (result.bytes is null) continue;
                await using var source = new MemoryStream(result.bytes, writable: false);
                try { image = await Image.LoadAsync(source, ct); break; }
                catch (UnknownImageFormatException) { }
            }
            if (image is not null) break;
        }
        if (image is null) return;
        using (image)
        {
            if (image.Width is < 1 or > 2048 || image.Height is < 1 or > 2048 || (long)image.Width * image.Height > 4_000_000) return;
            await using var sanitized = new MemoryStream();
            await image.SaveAsync(sanitized, new PngEncoder(), ct);
            if (sanitized.Length > MaximumDownloadBytes) return;
            var digest = Convert.ToHexString(SHA256.HashData(sanitized.ToArray())).ToLowerInvariant();
            var key = PublicAssetContracts.WebsiteIconStorageKey(asset.Id);
            sanitized.Position = 0;
            await storage.PublishAsync(string.Empty, sanitized, key, "image/png", ct, overwrite: false);
            asset.Publish(digest, "image/png", sanitized.Length, image.Width, image.Height, key, clock.GetCurrentInstant());
            try { await db.CommitAsync(ct); }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another delivery completed the same immutable revision first.
            }
        }
    }

    private sealed record IconCandidate(Uri Uri, int Score);

    private static IEnumerable<IconCandidate> ConventionalCandidates(Uri origin, int score)
    {
        for (var index = 0; index < ConventionalPaths.Length; index++)
            yield return new(new Uri(origin, ConventionalPaths[index]), score - index * 100);
    }

    internal static IReadOnlyList<Uri> DiscoverHtmlIconUris(Uri documentUri, string html) =>
        DiscoverHtmlIconCandidates(documentUri, html).OrderByDescending(x => x.Score).Select(x => x.Uri).ToArray();

    private static IReadOnlyList<IconCandidate> DiscoverHtmlIconCandidates(Uri documentUri, string html)
    {
        var baseUri = DocumentBaseUri(documentUri, html);

        var candidates = new List<IconCandidate>();
        foreach (Match tag in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150)))
        {
            var rel = Attribute(tag.Value, "rel")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var href = Attribute(tag.Value, "href");
            if (rel is null || href is null || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(baseUri, href, out var uri)) continue;
            var relation = rel.Select(x => x.ToLowerInvariant()).ToHashSet();
            var priority = relation.Contains("icon") ? 4_000
                : relation.Contains("apple-touch-icon") ? 3_500
                : relation.Contains("apple-touch-icon-precomposed") ? 3_400
                : 0;
            if (priority > 0) candidates.Add(new(uri, priority + PreferredSize(Attribute(tag.Value, "sizes"))));
        }

        var tile = Regex.Matches(html, "<meta\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
            .Cast<Match>().FirstOrDefault(tag => string.Equals(Attribute(tag.Value, "name"), "msapplication-TileImage", StringComparison.OrdinalIgnoreCase));
        if (tile is not null && Attribute(tile.Value, "content") is { } content && Uri.TryCreate(baseUri, content, out var tileUri))
            candidates.Add(new(tileUri, 2_500));
        return candidates;
    }

    internal static Uri? DiscoverManifestUri(Uri documentUri, string html)
    {
        var baseUri = DocumentBaseUri(documentUri, html);
        foreach (Match tag in Regex.Matches(html, "<link\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150)))
        {
            var rel = Attribute(tag.Value, "rel")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var href = Attribute(tag.Value, "href");
            if (rel?.Contains("manifest", StringComparer.OrdinalIgnoreCase) == true && href is not null && Uri.TryCreate(baseUri, href, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                return uri;
        }
        return null;
    }

    private static Uri DocumentBaseUri(Uri documentUri, string html)
    {
        var baseTag = Regex.Match(html, "<base\\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return baseTag.Success
               && Attribute(baseTag.Value, "href") is { } href
               && Uri.TryCreate(documentUri, href, out var parsed)
               && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed
            : documentUri;
    }

    internal static IReadOnlyList<Uri> DiscoverManifestIconUris(Uri manifestUri, byte[] json) =>
        DiscoverManifestIconCandidates(manifestUri, json).OrderByDescending(x => x.Score).Select(x => x.Uri).ToArray();

    private static IReadOnlyList<IconCandidate> DiscoverManifestIconCandidates(Uri manifestUri, byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return [];
            var result = new List<IconCandidate>();
            foreach (var icon in icons.EnumerateArray().Take(32))
            {
                if (icon.ValueKind != JsonValueKind.Object || !icon.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.String || !Uri.TryCreate(manifestUri, src.GetString(), out var uri)) continue;
                var purpose = icon.TryGetProperty("purpose", out var purposeValue) && purposeValue.ValueKind == JsonValueKind.String ? purposeValue.GetString()! : "any";
                var purposes = purpose.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (purposes.Contains("monochrome") && !purposes.Contains("any")) continue;
                var sizes = icon.TryGetProperty("sizes", out var sizesValue) && sizesValue.ValueKind == JsonValueKind.String ? sizesValue.GetString() : null;
                result.Add(new(uri, 3_000 + PreferredSize(sizes)));
            }
            return result;
        }
        catch (JsonException) { return []; }
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"\\b{Regex.Escape(name)}\\s*=\\s*(?:[\"'](?<quoted>[^\"']*)[\"']|(?<plain>[^\\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        return match.Success ? WebUtility.HtmlDecode(match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value) : null;
    }

    private static int PreferredSize(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes)) return 0;
        if (sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("any", StringComparer.OrdinalIgnoreCase)) return 512;
        return sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Regex.Match(value, "^(?<width>[0-9]{1,4})x(?<height>[0-9]{1,4})$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(20)))
            .Where(match => match.Success)
            .Select(match => Math.Min(int.Parse(match.Groups["width"].Value), int.Parse(match.Groups["height"].Value)))
            .Where(value => value is > 0 and <= 2048).DefaultIfEmpty().Max();
    }

    /// <summary>
    /// Keeps subdomain lookup exact first, then allows one bounded fallback to
    /// the registrable hostname (appleid.apple.com -> apple.com,
    /// auth.eu.ovhcloud.com -> ovhcloud.com). Common country-code second-level
    /// suffixes retain the third label (login.example.co.uk -> example.co.uk).
    /// This is a single derived candidate, never an unbounded suffix walk.
    /// </summary>
    internal static string? ParentHostname(string hostname)
    {
        var labels = hostname.Split('.');
        if (labels.Length < 3) return null;
        var registrableLabelCount = labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        if (labels.Length <= registrableLabelCount) return null;
        return string.Join('.', labels.Skip(labels.Length - registrableLabelCount));
    }

    private static Task<byte[]?> DownloadAsync(Uri initial, CancellationToken ct) =>
        RunBoundedAsync(token => DownloadCoreAsync(initial, token), TimeSpan.FromSeconds(5), ct);

    internal static async Task<T?> RunBoundedAsync<T>(Func<CancellationToken, Task<T?>> operation, TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return default; }
    }

    private static async Task<byte[]?> DownloadCoreAsync(Uri initial, CancellationToken operationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !PublicNetworkPolicy.IsValidHostname(current.IdnHost)) return null;
            var addresses = await Dns.GetHostAddressesAsync(current.IdnHost, operationToken);
            if (addresses.Length == 0 || addresses.Any(x => !PublicNetworkPolicy.IsPublic(x))) return null;

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Palladin-PublicAssetCatalog/1.0; +https://palladin.io)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null) return null;
                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumDownloadBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(operationToken);
            await using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, operationToken); if (read == 0) break;
                if (output.Length + read > MaximumDownloadBytes) return null;
                await output.WriteAsync(buffer.AsMemory(0, read), operationToken);
            }
            return output.ToArray();
        }
        return null;
    }

    private static async Task<byte[]?> TryDownloadCandidateAsync(Uri uri, CancellationToken ct)
    {
        try { return await DownloadAsync(uri, ct); }
        catch (HttpRequestException) { return null; }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}

internal static class PublicNetworkPolicy
{
    internal static bool IsValidHostname(string host) => PublicAssetCatalog.Features.PublicAssetContracts.TryHostname(host, out var normalized) && normalized == host && !IPAddress.TryParse(host, out _);

    internal static bool IsPublic(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal) return false;
            var b = ip.GetAddressBytes();
            return !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8); // documentation range
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var v = ip.GetAddressBytes();
        if (v[0] is 0 or 10 or 127 || v[0] >= 224) return false;
        if (v[0] == 100 && v[1] is >= 64 and <= 127) return false;
        if (v[0] == 169 && v[1] == 254) return false;
        if (v[0] == 172 && v[1] is >= 16 and <= 31) return false;
        if (v[0] == 192 && (v[1] == 168 || v[1] == 0 || v[1] == 2)) return false;
        if (v[0] == 198 && (v[1] is 18 or 19 || v[1] == 51 && v[2] == 100)) return false;
        if (v[0] == 203 && v[1] == 0 && v[2] == 113) return false;
        return true;
    }
}
