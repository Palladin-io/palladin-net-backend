using System.Net;
using System.Net.Http.Json;
using Palladin.Tests.Integrations.Features.Vault;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<EntrySharingRateLimitCollection>]
public sealed class SharedUnlockRateLimitTests(EntrySharingRateLimitApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_OfferRouteUsesTrailingSlashOrDifferentCasing_Then_ItSharesTheOfferBudget()
    {
        // Given
        using var client = CreateClient("192.0.2.221");
        await ExhaustAsync(client, 30, "api/account/shared-unlock/operations/", "API/Account/Shared-Unlock/Operations");

        // When
        using var response = await client.PostAsJsonAsync("api/account/shared-unlock/operations/", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_ActivityRouteUsesTrailingSlashOrDifferentCasing_Then_ItSharesTheActivityBudget()
    {
        // Given
        using var client = CreateClient("192.0.2.222");
        await ExhaustAsync(client, 60, "api/account/shared-unlock/authorizations/activity/", "API/Account/Shared-Unlock/Authorizations/Activity");

        // When
        using var response = await client.PostAsJsonAsync(
            "api/account/shared-unlock/authorizations/activity/", new { }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_SharedUnlockBudgetsAreExhausted_Then_SiblingRoutesStayUnlimited()
    {
        // Given
        using var client = CreateClient("192.0.2.223");
        await ExhaustAsync(client, 30, "api/account/shared-unlock/operations");
        await ExhaustAsync(client, 60, "api/account/shared-unlock/authorizations/activity");

        // When
        using var authorizations = await client.PostAsJsonAsync(
            "api/account/shared-unlock/authorizations", new { }, TestContext.Current.CancellationToken);
        using var links = await client.PostAsJsonAsync(
            "api/account/shared-unlock/links", new { }, TestContext.Current.CancellationToken);

        // Then
        authorizations.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        links.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task ExhaustAsync(HttpClient client, int permitLimit, params string[] routes)
    {
        for (var i = 0; i < permitLimit; i++)
        {
            using var allowed = await client.PostAsJsonAsync(routes[i % routes.Length], new { }, TestContext.Current.CancellationToken);
            allowed.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, routes[i % routes.Length]);
        }
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
