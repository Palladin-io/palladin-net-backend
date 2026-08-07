using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Audit.Infrastructure.Exports;

internal interface IAuditExportStorage
{
    Task UploadAsync(Stream content, string key, CancellationToken cancellationToken);
    Task<string> CreateDownloadUrlAsync(string key, CancellationToken cancellationToken);
}

internal sealed class AuditExportStorageOptions
{
    internal const string Position = "Modules:Audit:ExportStorage";
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = "eu-central-1";
    public string? Endpoint { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public bool ForcePathStyle { get; init; }
    public int DownloadUrlLifetimeMinutes { get; init; } = 1;
}

internal sealed class S3AuditExportStorage : IAuditExportStorage, IDisposable
{
    private readonly AuditExportStorageOptions _options;
    private readonly IClock _clock;
    private readonly AmazonS3Client _client;

    public S3AuditExportStorage(IOptions<AuditExportStorageOptions> options, IClock clock)
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

    public async Task UploadAsync(Stream content, string key, CancellationToken cancellationToken) =>
        _ = await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = content,
            ContentType = "text/csv",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            Headers = { CacheControl = "private, no-store" },
        }, cancellationToken);

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

    public void Dispose() => _client.Dispose();
}
