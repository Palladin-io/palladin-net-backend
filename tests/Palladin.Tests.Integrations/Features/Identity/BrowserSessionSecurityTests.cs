using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class BrowserSessionSecurityTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(null, "1")]
    [InlineData("null", "1")]
    [InlineData("https://evil.example.test", "1")]
    [InlineData("https://panel.example.test.evil.test", "1")]
    [InlineData("https://panel.example.test", null)]
    public async Task When_OriginOrHeaderIsUntrusted_Then_RejectWithoutCookie(string? origin, string? header)
    {
        // Given
        using var client = apiFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/browser/auth/refresh");
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (header is not null) request.Headers.Add("X-Palladin-Browser", header);
        request.Content = JsonContent.Create(new { });

        // When
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task When_RefreshingBrowserCookie_Then_ReturnsOnlyAccessAndSessionIdentity()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var token = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/refresh", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("refreshToken", out _).ShouldBeFalse();
        body.RootElement.GetProperty("sessionId").GetGuid().ShouldBe(token.SessionId ?? token.Id);
        body.RootElement.GetProperty("userId").GetGuid().ShouldBe(user.Id);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        cookie.Contains("secure", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        cookie.Contains("path=/", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        cookie.Contains("domain=", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task When_OnlyJsonRefreshTokenIsSentToBrowserRoute_Then_ItCannotAuthenticate()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/refresh", new { refreshToken = raw });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
    [Fact]
    public async Task When_RefreshCookieBelongsToAnotherTabSession_Then_RejectWithoutReplacingCookie()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));
        var response = await client.PostAsJsonAsync("api/browser/auth/refresh", new { expectedSessionId = Guid.NewGuid() });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://panel.example.test", true)]
    [InlineData("https://sibling.example.test", false)]
    [InlineData("null", false)]
    public async Task When_BrowserPreflights_Then_CredentialsAreAllowedOnlyForExactConfiguredOrigins(string origin, bool allowed)
    {
        using var client = apiFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "api/browser/auth/refresh");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-palladin-browser");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBe(allowed);
        if (allowed)
        {
            response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(origin);
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single().ShouldBe("true");
        }
        else response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

}
