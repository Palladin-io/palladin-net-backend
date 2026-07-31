using System.Net;
using System.Net.Http.Json;
using System.Text;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Options;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

public sealed class GoogleOAuthProviderTests
{
    private const string OurClientId = "our-client-id.apps.googleusercontent.com";

    // The web implicit flow presents an opaque access token; it is accepted only after tokeninfo confirms it
    // was minted for OUR client. A token issued to a different client must be rejected (confused-deputy).
    [Fact]
    public async Task When_AccessTokenMintedForAnotherClient_Then_Rejected()
    {
        // Given
        var provider = CreateProvider(tokenInfoAud: "attacker-client-id.apps.googleusercontent.com", userInfoJson: null);

        // When / Then
        await Should.ThrowAsync<InvalidJwtException>(
            () => provider.ValidateTokenAsync("ya29-opaque-access-token", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_AccessTokenAudienceMatchesOurClient_Then_ReturnsUser()
    {
        // Given
        var provider = CreateProvider(
            tokenInfoAud: OurClientId,
            userInfoJson: """{"sub":"google-123","email":"user@example.com","email_verified":true,"name":"Jane","picture":"pic"}""");

        // When
        var result = await provider.ValidateTokenAsync("ya29-opaque-access-token", TestContext.Current.CancellationToken);

        // Then
        result.SubjectId.ShouldBe("google-123");
        result.Email.ShouldBe("user@example.com");
        result.EmailVerified.ShouldBeTrue();
        result.Name.ShouldBe("Jane");
    }

    private static GoogleOAuthProvider CreateProvider(string tokenInfoAud, string? userInfoJson)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(tokenInfoAud, userInfoJson)));

        return new GoogleOAuthProvider(Options.Create(new GoogleOAuthOptions { ClientId = OurClientId }), factory);
    }

    private sealed class StubHandler(string tokenInfoAud, string? userInfoJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var response = url.Contains("tokeninfo", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { aud = tokenInfoAud }) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(userInfoJson ?? "{}", Encoding.UTF8, "application/json"),
                };

            return Task.FromResult(response);
        }
    }
}
