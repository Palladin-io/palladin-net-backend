using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;

namespace Palladin.Tests.Unit.PublicAssetCatalog;

public sealed class PublicAssetUploadEndpointTests
{
    [Theory]
    [InlineData(null, "internal.example.test", 4566)]
    [InlineData("http://127.0.0.1:54574", "127.0.0.1", 54574)]
    public async Task When_Upload_Endpoint_Is_Configured_Then_Signing_Uses_That_Authority(
        string? uploadEndpoint, string host, int port)
    {
        // Given
        var storage = new S3PublicAssetStorage(Options.Create(new PublicAssetStorageOptions
        {
            BucketName = "test-icons",
            Region = "eu-west-1",
            Endpoint = "http://internal.example.test:4566",
            UploadEndpoint = uploadEndpoint,
            PublicBaseUrl = "https://assets.example.test",
            ForcePathStyle = true,
            AccessKey = "test",
            SecretKey = "test",
        }));
        // When
        var url = new Uri(await storage.CreateUploadUrlAsync("staging/icon.png", "image/png",
            SystemClock.Instance.GetCurrentInstant() + Duration.FromMinutes(5), TestContext.Current.CancellationToken));
        // Then
        url.Host.ShouldBe(host);
        url.Port.ShouldBe(port);
        url.AbsolutePath.ShouldBe("/test-icons/staging/icon.png");
        storage.GetDeliveryUrl("published/icon.png").ShouldBe("https://assets.example.test/published/icon.png");
    }
}
