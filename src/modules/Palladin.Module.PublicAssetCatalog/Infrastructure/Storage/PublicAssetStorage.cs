using System.Net;
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
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public bool ForcePathStyle { get; init; }
    public Duration UploadExpiry { get; init; } = Duration.FromMinutes(5);
    public long MaximumBytes { get; init; } = 1024 * 1024;
}
internal sealed record StagedObject(Stream Content, long Length, string? MediaType);
internal interface IPublicAssetStorage
{
    Task<string> CreateUploadUrlAsync(string key, string mediaType, Instant expiresAt, CancellationToken ct);
    Task<StagedObject?> OpenStagedAsync(string key, CancellationToken ct);
    Task PublishAsync(string stagingKey, Stream validatedContent, string publishedKey, string mediaType, CancellationToken ct, bool overwrite = true);
    Task DeleteStagedAsync(string stagingKey, CancellationToken ct);
    string GetDeliveryUrl(string key);
}
internal sealed class S3PublicAssetStorage(IOptions<PublicAssetStorageOptions> configured) : IPublicAssetStorage
{
    private readonly PublicAssetStorageOptions options = configured.Value;
    private readonly IAmazonS3 s3 = Build(configured.Value);
    public async Task<string> CreateUploadUrlAsync(string key, string mediaType, Instant expiresAt, CancellationToken ct) =>
        await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = options.BucketName, Key = key, Verb = HttpVerb.PUT, Expires = expiresAt.ToDateTimeUtc(), ContentType = mediaType, Protocol = options.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true ? Protocol.HTTP : Protocol.HTTPS });
    public async Task<StagedObject?> OpenStagedAsync(string key, CancellationToken ct)
    {
        try { var response = await s3.GetObjectAsync(options.BucketName, key, ct); return new(response.ResponseStream, response.ContentLength, response.Headers.ContentType); }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task PublishAsync(string stagingKey, Stream validatedContent, string publishedKey, string mediaType, CancellationToken ct, bool overwrite = true)
    {
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.BucketName, Key = publishedKey, InputStream = validatedContent, AutoCloseStream = false, ContentType = mediaType, IfNoneMatch = overwrite ? null : "*", Headers = { CacheControl = "public,max-age=31536000,immutable" } }, ct);
        if (!string.IsNullOrEmpty(stagingKey)) await s3.DeleteObjectAsync(options.BucketName, stagingKey, ct);
    }
    public Task DeleteStagedAsync(string stagingKey, CancellationToken ct) => s3.DeleteObjectAsync(options.BucketName, stagingKey, ct);
    public string GetDeliveryUrl(string key)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return $"{options.PublicBaseUrl.TrimEnd('/')}/{key}";
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) return options.ForcePathStyle ? $"{options.Endpoint.TrimEnd('/')}/{options.BucketName}/{key}" : $"{options.Endpoint.TrimEnd('/')}/{key}";
        return $"https://{options.BucketName}.s3.{options.Region}.amazonaws.com/{key}";
    }
    private static IAmazonS3 Build(PublicAssetStorageOptions o)
    {
        var c = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
        if (o.Endpoint is not null) { c.ServiceURL = o.Endpoint; c.AuthenticationRegion = o.Region; } else c.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
        return o.AccessKey is not null && o.SecretKey is not null ? new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), c) : new AmazonS3Client(c);
    }
}
