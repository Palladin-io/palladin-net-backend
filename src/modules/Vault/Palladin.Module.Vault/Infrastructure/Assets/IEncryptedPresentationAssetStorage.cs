using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Vault.Infrastructure.Assets;

internal interface IEncryptedPresentationAssetStorage
{
    Task PutAsync(Stream ciphertext, string key, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
    Task<string> CreateDownloadUrlAsync(string key, CancellationToken cancellationToken);
    Task DeleteAllVersionsAsync(string key, CancellationToken cancellationToken);
}

internal interface ILegacyPresentationAssetStorage
{
    Task DeleteAllVersionsAsync(string prefix, CancellationToken cancellationToken);
    Task<bool> HasVersionsAsync(string prefix, CancellationToken cancellationToken);
}

internal sealed class VaultAssetStorageOptions
{
    internal const string Position = "Modules:Vault:AssetStorage";
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = "eu-central-1";
    public string? Endpoint { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public bool ForcePathStyle { get; init; }
    public int DownloadUrlLifetimeMinutes { get; init; } = 1;
}

internal sealed class S3VaultAssetStorage : IEncryptedPresentationAssetStorage, ILegacyPresentationAssetStorage, IDisposable
{
    private readonly VaultAssetStorageOptions _options;
    private readonly IClock _clock;
    private readonly AmazonS3Client _client;

    public S3VaultAssetStorage(IOptions<VaultAssetStorageOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
        var config = new AmazonS3Config { ForcePathStyle = _options.ForcePathStyle };
        if (_options.Endpoint is { Length: > 0 }) config.ServiceURL = _options.Endpoint;
        else config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region);
        _client = _options.AccessKey is { } accessKey && _options.SecretKey is { } secretKey
            ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config)
            : new AmazonS3Client(config);
    }

    public async Task PutAsync(Stream ciphertext, string key, CancellationToken cancellationToken) =>
        _ = await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = ciphertext,
            ContentType = "application/octet-stream",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            Headers = { CacheControl = "private, no-store" },
        }, cancellationToken);

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _client.GetObjectMetadataAsync(_options.BucketName, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task<string> CreateDownloadUrlAsync(string key, CancellationToken cancellationToken) =>
        _client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = _clock.GetCurrentInstant().Plus(Duration.FromMinutes(_options.DownloadUrlLifetimeMinutes)).ToDateTimeUtc(),
            Protocol = _options.Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? Protocol.HTTP
                : Protocol.HTTPS,
        });

    public async Task DeleteAllVersionsAsync(string prefix, CancellationToken cancellationToken)
    {
        foreach (var batch in (await ListVersionsAsync(prefix, cancellationToken)).Chunk(1000))
        {
            var response = await _client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _options.BucketName,
                Objects = batch.Select(x => new KeyVersion { Key = x.Key, VersionId = x.VersionId }).ToList(),
            }, cancellationToken);
            if (response.DeleteErrors is { Count: > 0 })
                throw new InvalidOperationException($"Object storage rejected {response.DeleteErrors.Count} version deletions.");
        }
    }

    public async Task<bool> HasVersionsAsync(string prefix, CancellationToken cancellationToken) =>
        (await ListVersionsAsync(prefix, cancellationToken)).Count != 0;

    private async Task<IReadOnlyList<(string Key, string? VersionId)>> ListVersionsAsync(string prefix, CancellationToken ct)
    {
        var result = new List<(string, string?)>();
        var request = new ListVersionsRequest { BucketName = _options.BucketName, Prefix = prefix };
        ListVersionsResponse response;
        do
        {
            response = await _client.ListVersionsAsync(request, ct);
            result.AddRange((response.Versions ?? []).Select(x => (x.Key, x.VersionId)));
            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        } while (response.IsTruncated == true);
        return result;
    }

    public void Dispose() => _client.Dispose();
}
