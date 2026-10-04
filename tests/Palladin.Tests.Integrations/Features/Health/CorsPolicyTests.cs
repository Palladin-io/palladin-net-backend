using System.Net;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Health;

[Collection<ApiFactoryCollection>]
public sealed class CorsPolicyTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ConfiguredPanelSendsPreflight_Then_OriginIsAllowed()
    {
        // Given
        const string origin = "https://panel.example.test";
        var request = new HttpRequestMessage(HttpMethod.Options, "api/auth/login/salt");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        // When
        var response = await apiFactory.CreateClient()
            .SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([origin]);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);
    }

    [Theory]
    [InlineData("https://panel.example.test.attacker.test")]
    [InlineData("https://panel.example.test:8443")]
    [InlineData("http://panel.example.test")]
    public async Task When_UnknownOriginSendsPreflight_Then_OriginIsNotAllowed(string origin)
    {
        // Given
        var request = new HttpRequestMessage(HttpMethod.Options, "api/auth/login/salt");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        // When
        var response = await apiFactory.CreateClient()
            .SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }
}
