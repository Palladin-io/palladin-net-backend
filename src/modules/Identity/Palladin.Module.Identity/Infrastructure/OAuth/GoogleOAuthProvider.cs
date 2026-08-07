using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.Options;
using Google.Apis.Auth;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.OAuth;

[UsedImplicitly]
internal sealed class GoogleOAuthProvider(
    IOptions<GoogleOAuthOptions> options,
    IHttpClientFactory httpClientFactory) : IExternalOAuthProvider
{
    public AuthProvider Provider => AuthProvider.Google;

    public async Task<ExternalUserInfo> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            return await ValidateIdTokenAsync(token);
        }
        catch (InvalidJwtException)
        {
            return await ValidateAccessTokenAsync(token, ct);
        }
    }

    private async Task<ExternalUserInfo> ValidateIdTokenAsync(string idToken)
    {
        var payload = await GoogleJsonWebSignature.ValidateAsync(
            idToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = [options.Value.ClientId] });

        return new ExternalUserInfo(
            payload.Subject, payload.Email, payload.EmailVerified, payload.Name, payload.Picture);
    }

    private async Task<ExternalUserInfo> ValidateAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();

        // Reject an access token minted for a different OAuth client — without this audience check any valid
        // Google access token could be replayed here to impersonate its owner (a confused-deputy attack).
        var tokenInfo = await client.GetFromJsonAsync<GoogleTokenInfoResponse>(
            $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}", ct);
        if (tokenInfo?.Aud != options.Value.ClientId)
        {
            throw new InvalidJwtException("Access token was not issued for this application.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var userInfo = await response.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(ct)
            ?? throw new InvalidJwtException("Google userinfo returned no profile.");

        return new ExternalUserInfo(
            userInfo.Sub, userInfo.Email, userInfo.EmailVerified, userInfo.Name, userInfo.Picture);
    }

    private sealed record GoogleTokenInfoResponse(
        [property: JsonPropertyName("aud")] string? Aud);

    private sealed record GoogleUserInfoResponse(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("email_verified")] bool EmailVerified,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picture")] string? Picture);
}
