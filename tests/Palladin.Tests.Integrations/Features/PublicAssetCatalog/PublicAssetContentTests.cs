using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.PublicAssetCatalog;

[Collection<ApiFactoryCollection>]
public sealed class PublicAssetContentTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_PublishedRevisionIsRequestedAnonymously_Then_ReturnsImmutableImageBytes()
    {
        // Given
        var asset = await SeedAsync(true);
        byte[] image = [137, 80, 78, 71];
        apiFactory.PublicAssetStorage.OpenImmutableAsync(asset.Revisions[0].StorageKey, "image/png", Arg.Any<CancellationToken>())
            .Returns(new ImmutablePublishedObject(image, "digest", image.Length));

        // When
        var response = await apiFactory.Client.GetAsync($"/api/public-assets/{asset.Id}/revisions/1/content", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(image);
        response.Headers.CacheControl!.ToString().ShouldContain("immutable");
        response.Headers.Location.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task When_RevisionIsNotPublished_Then_ReturnsNotFound(bool published, int revision)
    {
        // Given
        var asset = await SeedAsync(published);

        // When
        var response = await apiFactory.Client.GetAsync($"/api/public-assets/{asset.Id}/revisions/{revision}/content", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await apiFactory.PublicAssetStorage.DidNotReceive().OpenImmutableAsync($"published/test/{asset.Id:N}/1.png", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_PublishedObjectIsMissing_Then_ReturnsNotFoundWithoutRedirect()
    {
        // Given
        var asset = await SeedAsync(true);

        // When
        var response = await apiFactory.Client.GetAsync($"/api/public-assets/{asset.Id}/revisions/1/content", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.Location.ShouldBeNull();
    }

    private async Task<PublicAsset> SeedAsync(bool published)
    {
        using var scope = apiFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PublicAssetCatalogDbWriteContext>();
        var asset = PublicAsset.Create(Guid.NewGuid(), "Synthetic icon", []);
        if (published)
        {
            asset.Publish("digest", "image/png", 4, 1, 1, $"published/test/{asset.Id:N}/1.png", apiFactory.FakeClock.GetCurrentInstant());
        }
        db.Assets.Add(asset);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return asset;
    }
}
