using System.Net;
using System.Net.Http.Json;
using Palladin.Tests.Integrations.Features.Vault;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<EntrySharingRateLimitCollection>]
public sealed class BrowserSessionRateLimitTests(EntrySharingRateLimitApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AlternatingNativeBrowserAndMigrationRoutes_Then_TheyShareOneAuthBudget()
    {
        using var client = new HttpClient(apiFactory.CreateHandler(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.198");
        })) { BaseAddress = new Uri("http://localhost") };
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        var routes = new[] { "api/auth/refresh", "api/browser/auth/refresh", "api/browser/auth/migrate" };
        for (var i = 0; i < 30; i++)
        {
            using var response = await client.PostAsJsonAsync(routes[i % routes.Length], new { refreshToken = "synthetic-invalid" }, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, routes[i % routes.Length]);
        }
        foreach (var route in routes)
        {
            using var response = await client.PostAsJsonAsync(route, new { refreshToken = "synthetic-invalid" }, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter.ShouldNotBeNull();
            response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        }
    }
}
