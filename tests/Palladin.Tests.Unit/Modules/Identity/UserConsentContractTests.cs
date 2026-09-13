using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Features;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class UserConsentContractTests
{
    [Fact]
    public void When_ConsentResponseIsSerialized_Then_ItMatchesTheVersionedClientFixture()
    {
        // Given: a continuous grant can have a newer decision revision than activation epoch.
        var response = new UserConsentsResponse([
            new UserConsentResponse("product_analytics", "palladin_web_mobile", "granted", 3, 1,
                Instant.FromUtc(2026, 9, 11, 12, 0), "test-v1", "en",
                new UserConsentNoticeResponse("test-v1", "en", "Test analytics consent.")),
            new UserConsentResponse("email_marketing", "palladin_email_news_and_offers", "unknown", 0, 0,
                null, null, null, null),
        ], 60);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Consents", "v1.json")));

        // When
        var actual = JsonSerializer.SerializeToNode(response, PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        JsonNode.DeepEquals(actual, expected).ShouldBeTrue();
    }
}
