using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;

internal sealed class PublicAssetStorageOptions
{
    public const string Position = "Modules:PublicAssetCatalog:Storage";
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = "eu-central-1";
    public string? PublicBaseUrl { get; init; }
    public string? Endpoint { get; init; }
    public string? UploadEndpoint { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public bool ForcePathStyle { get; init; }
    public Duration UploadExpiry { get; init; } = Duration.FromMinutes(5);
    public long MaximumBytes { get; init; } = 1024 * 1024;
}
internal sealed record StagedObject(Stream Content, long Length, string? MediaType);
internal sealed record ImmutablePublishedObject(byte[]? ExistingContent, string Digest, long Length);
internal interface IPublicAssetStorage
{
    Task<string> CreateUploadUrlAsync(string key, string mediaType, Instant expiresAt, CancellationToken ct);
    Task<StagedObject?> OpenStagedAsync(string key, CancellationToken ct);
    Task<ImmutablePublishedObject?> OpenImmutableAsync(string publishedKey, string mediaType, CancellationToken ct);
    Task PublishAsync(string stagingKey, Stream validatedContent, string publishedKey, string mediaType, CancellationToken ct, bool overwrite = true);
    Task<ImmutablePublishedObject> PublishImmutableAsync(Stream validatedContent, string publishedKey, string mediaType, string digest, long length, CancellationToken ct);
    Task DeleteStagedAsync(string stagingKey, CancellationToken ct);
    string GetDeliveryUrl(string key);
}
internal sealed class S3PublicAssetStorage(IOptions<PublicAssetStorageOptions> configured) : IPublicAssetStorage
{
    private readonly PublicAssetStorageOptions options = configured.Value;
    private readonly IAmazonS3 s3 = Build(configured.Value);
    public async Task<string> CreateUploadUrlAsync(string key, string mediaType, Instant expiresAt, CancellationToken ct)
    {
        var request = new GetPreSignedUrlRequest { BucketName = options.BucketName, Key = key, Verb = HttpVerb.PUT, Expires = expiresAt.ToDateTimeUtc(), ContentType = mediaType, Protocol = (options.UploadEndpoint ?? options.Endpoint)?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS };
        if (options.UploadEndpoint is null)
        {
            return await s3.GetPreSignedURLAsync(request);
        }
        using var uploadSigner = Build(options, options.UploadEndpoint);
        return await uploadSigner.GetPreSignedURLAsync(request);
    }
    public async Task<StagedObject?> OpenStagedAsync(string key, CancellationToken ct)
    {
        try { var response = await s3.GetObjectAsync(options.BucketName, key, ct); return new(response.ResponseStream, response.ContentLength, response.Headers.ContentType); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task<ImmutablePublishedObject?> OpenImmutableAsync(string publishedKey, string mediaType, CancellationToken ct)
    {
        try
        {
            using var existing = await s3.GetObjectAsync(options.BucketName, publishedKey, ct);
            if (existing.ContentLength is < 1 || existing.ContentLength > options.MaximumBytes
                || !string.Equals(existing.Headers.ContentType, mediaType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Immutable public asset '{publishedKey}' has invalid metadata.");
            await using var content = new MemoryStream((int)existing.ContentLength);
            await existing.ResponseStream.CopyToAsync(content, ct);
            if (content.Length != existing.ContentLength)
                throw new InvalidOperationException($"Immutable public asset '{publishedKey}' has an invalid length.");
            var bytes = content.ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(bytes, digest, bytes.LongLength);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
    public async Task PublishAsync(string stagingKey, Stream validatedContent, string publishedKey, string mediaType, CancellationToken ct, bool overwrite = true)
    {
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.BucketName, Key = publishedKey, InputStream = validatedContent, AutoCloseStream = false, ContentType = mediaType, IfNoneMatch = overwrite ? null : "*", Headers = { CacheControl = "public,max-age=31536000,immutable" } }, ct);
        if (!string.IsNullOrEmpty(stagingKey)) await s3.DeleteObjectAsync(options.BucketName, stagingKey, ct);
    }
    public async Task<ImmutablePublishedObject> PublishImmutableAsync(Stream validatedContent, string publishedKey, string mediaType, string digest, long length, CancellationToken ct)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = options.BucketName,
                Key = publishedKey,
                InputStream = validatedContent,
                AutoCloseStream = false,
                ContentType = mediaType,
                IfNoneMatch = "*",
                Headers = { CacheControl = "public,max-age=31536000,immutable" },
            };
            await s3.PutObjectAsync(request, ct);
            return new(null, digest, length);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // The first immutable write is authoritative. A previous delivery
            // may have stored it and crashed before committing the aggregate,
            // while the mutable upstream favicon may since have changed.
            return await OpenImmutableAsync(publishedKey, mediaType, ct)
                ?? throw new InvalidOperationException($"Immutable public asset '{publishedKey}' disappeared after a write conflict.", exception);
        }
    }
    public Task DeleteStagedAsync(string stagingKey, CancellationToken ct) => s3.DeleteObjectAsync(options.BucketName, stagingKey, ct);
    public string GetDeliveryUrl(string key)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return $"{options.PublicBaseUrl.TrimEnd('/')}/{key}";
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) return options.ForcePathStyle ? $"{options.Endpoint.TrimEnd('/')}/{options.BucketName}/{key}" : $"{options.Endpoint.TrimEnd('/')}/{key}";
        return $"https://{options.BucketName}.s3.{options.Region}.amazonaws.com/{key}";
    }
    private static IAmazonS3 Build(PublicAssetStorageOptions o, string? endpoint = null)
    {
        var c = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
        if ((endpoint ?? o.Endpoint) is { } serviceUrl) { c.ServiceURL = serviceUrl; c.AuthenticationRegion = o.Region; } else c.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
        return o.AccessKey is not null && o.SecretKey is not null ? new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), c) : new AmazonS3Client(c);
    }
}
