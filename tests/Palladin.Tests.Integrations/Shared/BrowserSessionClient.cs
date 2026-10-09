using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Extensions;

namespace Palladin.Tests.Integrations.Shared;

internal static class BrowserSessionClient
{
    internal static async Task<(HttpClient Client, Guid SessionId)> CreateAsync(ApiFactory factory, User user, string raw)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var hash = TokenService.HashToken(raw);
        var token = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens
            .SingleAsync(token => token.TokenHash == hash, TestContext.Current.CancellationToken);
        var client = factory.CreateAuthenticatedClient(user);
        var sessionId = token.SessionId ?? token.Id;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            scope.ServiceProvider.GetRequiredService<ITokenService>().BindBrowserSession(
                client.DefaultRequestHeaders.Authorization!.Parameter!, sessionId));
        client.DefaultRequestHeaders.Add("Origin", "https://panel.example.test");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-palladin-refresh=" + Uri.EscapeDataString(raw));
        return (client, sessionId);
    }
}
