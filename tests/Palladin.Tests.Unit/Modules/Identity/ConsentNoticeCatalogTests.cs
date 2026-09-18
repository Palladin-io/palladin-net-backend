using Palladin.Module.Identity.Infrastructure.Consents;
using Shouldly;
using NodaTime;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class ConsentNoticeCatalogTests
{
    [Fact]
    public void When_ClientNoticeIsReleased_Then_AnEmptyTextCatalogueDoesNotBlockIt()
    {
        // Given
        var catalogue = new ConsentNoticeCatalog();

        // When
        var notice = catalogue.Find("product_analytics", "en", "2026-09-18T00:00:00Z", Instant.FromUtc(2026, 9, 18, 0, 0));

        // Then
        notice.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("product_analytics", "pl", "2026-09-18T00:00:00Z", 18, true)]
    [InlineData("email_marketing", "en", "2026-09-18T00:00:00Z", 18, true)]
    [InlineData("product_analytics", "en", "2026-09-18T00:00:00Z", 17, false)]
    [InlineData("product_analytics", "en", "2026-09-17T00:00:00Z", 18, false)]
    [InlineData("product_analytics", "de", "2026-09-18T00:00:00Z", 18, false)]
    [InlineData("unknown", "en", "2026-09-18T00:00:00Z", 18, false)]
    public void When_VersionIsSubmitted_Then_OnlyItsReleasedPurposeAndLocaleAreAccepted(
        string purpose, string locale, string version, int day, bool accepted)
    {
        // Given
        var catalogue = new ConsentNoticeCatalog();

        // When
        var notice = catalogue.Find(purpose, locale, version, Instant.FromUtc(2026, 9, day, 0, 0));

        // Then
        (notice is not null).ShouldBe(accepted);
    }

    [Fact]
    public void When_AnOlderDisplayedVersionIsSubmitted_Then_ItIsNotReplacedByTheLatestVersion()
    {
        // Given
        var catalogue = new ConsentNoticeCatalog([
            new("product_analytics", "palladin_web_mobile", "2026-09-10T00:00:00Z", "en"),
            new("product_analytics", "palladin_web_mobile", "2026-09-18T00:00:00Z", "en"),
        ]);

        // When
        var notice = catalogue.Find("product_analytics", "en", "2026-09-10T00:00:00Z", Instant.FromUtc(2026, 9, 19, 0, 0));

        // Then
        notice!.Version.ShouldBe("2026-09-10T00:00:00Z");
    }
}
