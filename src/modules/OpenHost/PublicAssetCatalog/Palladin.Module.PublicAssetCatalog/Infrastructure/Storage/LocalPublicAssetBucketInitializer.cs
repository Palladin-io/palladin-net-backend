using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;

/// <summary>Creates only the explicitly configured LocalStack development bucket.</summary>
internal sealed class LocalPublicAssetBucketInitializer(IOptions<PublicAssetStorageOptions> configured) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var options = configured.Value;
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey)) return;
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config { ServiceURL = options.Endpoint, ForcePathStyle = true, AuthenticationRegion = options.Region });
        try
        {
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.BucketName))
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.BucketName, BucketRegionName = options.Region }, ct);
        }
        catch (HttpRequestException) { return; } // LocalStack is optional during non-storage tests.

        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = options.BucketName,
            Policy = $$"""
            {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":"*","Action":"s3:GetObject","Resource":"arn:aws:s3:::{{options.BucketName}}/published/*"}]}
            """,
        }, ct);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = options.BucketName,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedMethods = ["PUT"],
                        AllowedOrigins =
                        [
                            "http://localhost:5173",
                            "http://127.0.0.1:5173",
                            "http://localhost:5174",
                            "http://127.0.0.1:5174",
                        ],
                        AllowedHeaders = ["*"],
                    },
                ],
            },
        }, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
