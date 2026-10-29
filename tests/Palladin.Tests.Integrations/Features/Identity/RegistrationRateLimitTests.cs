using System.Net;
using System.Net.Http.Json;
using Palladin.Tests.Integrations.Features.Vault;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<EntrySharingRateLimitCollection>]
public sealed class RegistrationRateLimitTests(EntrySharingRateLimitApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_SixthRegistrationFromOneAddressWithinWindow_Then_Returns429AcrossNativeAndBrowserRoutes()
    {
        // Given
        using var client = CreateClient("192.0.2.211");
        var routes = new[] { "api/auth/register", "api/browser/auth/register" };
        for (var i = 0; i < 5; i++)
        {
            using var allowed = await client.PostAsJsonAsync(routes[i % routes.Length], new { }, TestContext.Current.CancellationToken);
            allowed.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, routes[i % routes.Length]);
        }

        // When
        using var response = await client.PostAsJsonAsync("api/auth/register", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_FourthVerificationResendFromOneAddressWithinWindow_Then_Returns429()
    {
        // Given
        using var client = CreateClient("192.0.2.212");
        for (var i = 0; i < 3; i++)
        {
            using var allowed = await client.PostAsJsonAsync("api/auth/verify-email/resend", new { }, TestContext.Current.CancellationToken);
            allowed.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        // When
        using var response = await client.PostAsJsonAsync("api/auth/verify-email/resend", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_RegistrationBudgetIsExhausted_Then_LoginKeepsItsOwnThirtyRequestBudget()
    {
        // Given
        using var client = CreateClient("192.0.2.213");
        for (var i = 0; i < 6; i++)
        {
            using var register = await client.PostAsJsonAsync("api/auth/register", new { }, TestContext.Current.CancellationToken);
        }

        // When
        for (var i = 0; i < 30; i++)
        {
            using var login = await client.PostAsJsonAsync("api/auth/login", new { }, TestContext.Current.CancellationToken);
            login.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        using var response = await client.PostAsJsonAsync("api/auth/login", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private HttpClient CreateClient(string remoteAddress)
    {
        var client = new HttpClient(apiFactory.CreateHandler(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        })) { BaseAddress = new Uri("http://localhost") };
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        client.DefaultRequestHeaders.Add("X-Palladin-Browser", "1");
        return client;
    }
}
