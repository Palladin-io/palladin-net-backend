using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class BrowserSessionTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_PasswordLoginSucceeds_Then_IssuesCookieWithoutRefreshJson()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var credential = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var email = $"browser-{Guid.NewGuid():N}@example.com";
        await apiFactory.Services.SeedPasswordUserAsync(credential, email: email, emailVerified: true);
        using var client = BrowserClient();

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/login", new {
            email, securityVersion = 1, kdfProfileId = "identity-argon2id-password-v1",
            authCredential = Convert.ToBase64String(credential).TrimEnd('=').Replace('+', '-').Replace('/', '_') });

        // Then
        await AssertBrowserSession(response);
    }

    [Fact]
    public async Task When_OAuthLoginSucceeds_Then_IssuesCookieWithoutRefreshJson()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var id = Guid.NewGuid().ToString("N");
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo(id, id + "@example.com", true, "Browser User", null));
        using var client = BrowserClient();

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/oauth/google", new { token = "synthetic-oauth-proof" });

        // Then
        await AssertBrowserSession(response);
    }

    [Fact]
    public async Task When_LegacySessionMigrates_Then_OldTokenIsRevokedAndCookieIsIssued()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var old = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = BrowserClient();

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/migrate", new { refreshToken = raw });

        // Then
        await AssertBrowserSession(response);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens.SingleAsync(t => t.Id == old.Id);
        stored.RevokedAt.ShouldNotBeNull();
        stored.ReplacedByTokenId.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_CookieAlreadyExists_Then_MigrationCannotReplaceIt()
    {
        // Given
        using var client = BrowserClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=existing");

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/migrate", new { refreshToken = "synthetic-legacy" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task When_LoggingOutWithoutJwt_Then_RevokesOnlyCookieSession()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var token = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        var unrelated = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        using var client = BrowserClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/logout", new { expectedSessionId = token.SessionId ?? token.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.Contains("Set-Cookie").ShouldBeTrue();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await context.RefreshTokens.SingleAsync(t => t.Id == token.Id)).RevokedAt.ShouldNotBeNull();
        (await context.RefreshTokens.SingleAsync(t => t.Id == unrelated.Id)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_LogoutTargetsOlderSession_Then_DoesNotClearNewerCookie()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var token = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = BrowserClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/logout", new { expectedSessionId = Guid.NewGuid() });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens.SingleAsync(t => t.Id == token.Id)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_DiscardingUninstalledSession_Then_NewCookieSessionSurvives()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var old = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, raw);
        using var client = BrowserClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));
        var refreshed = await client.PostAsJsonAsync("api/browser/auth/refresh", new { });
        using var body = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        var access = body.RootElement.GetProperty("accessToken").GetString();
        var nextRaw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var next = await apiFactory.Services.SeedRefreshTokenAsync(user.Id, nextRaw);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(nextRaw));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);

        // When
        var response = await client.PostAsJsonAsync("api/browser/auth/discard", new { });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await context.RefreshTokens.SingleAsync(t => t.Id == old.Id)).RevokedAt.ShouldNotBeNull();
        (await context.RefreshTokens.SingleAsync(t => t.Id == next.Id)).RevokedAt.ShouldBeNull();
    }

    private HttpClient BrowserClient()
    {
        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        return client;
    }

    private static async Task AssertBrowserSession(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("refreshToken", out _).ShouldBeFalse();
        body.RootElement.GetProperty("sessionId").GetGuid().ShouldNotBe(Guid.Empty);
        body.RootElement.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
        response.Headers.Contains("Set-Cookie").ShouldBeTrue();
    }
}
