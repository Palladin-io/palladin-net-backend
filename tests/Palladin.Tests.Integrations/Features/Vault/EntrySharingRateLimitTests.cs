using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<EntrySharingRateLimitCollection>]
public sealed class EntrySharingRateLimitTests(EntrySharingRateLimitApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_IdsRoutesAndForwardedHeadersChange_Then_TheSamePeerSharesOneBudget()
    {
        // Given
        apiFactory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(Environments.Development);
        using var client = CreateClient("192.0.2.11");

        // When
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var request = Request(attempt);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{attempt + 1}");
            request.Headers.TryAddWithoutValidation("Forwarded", $"for=198.51.100.{attempt + 1}");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Then
        for (var attempt = 60; attempt < 67; attempt++)
        {
            using var request = Request(attempt);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            await AssertRejectedAsync(response);
        }
    }

    [Fact]
    public async Task When_RequestsRaceForOneWindow_Then_OnlySixtyReachTheEndpoints()
    {
        // Given
        using var client = CreateClient("192.0.2.12");

        // When
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 80).Select(async attempt =>
        {
            using var request = Request(attempt);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await AssertRejectedAsync(response);
            }

            return response.StatusCode;
        }));

        // Then
        outcomes.Count(status => status == HttpStatusCode.NotFound).ShouldBe(60);
        outcomes.Count(status => status == HttpStatusCode.TooManyRequests).ShouldBe(20);
    }

    [Fact]
    public async Task When_OnePeerIsLimited_Then_OtherPeersAndNonSharingRoutesRemainAvailable()
    {
        // Given
        using var limited = CreateClient("192.0.2.13");
        using var otherPeer = CreateClient("192.0.2.14");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var request = Request(attempt);
            using var response = await limited.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // When
        using var blockedRequest = Request(60);
        using var blocked = await limited.SendAsync(blockedRequest, TestContext.Current.CancellationToken);
        using var otherRequest = Request(60);
        using var admitted = await otherPeer.SendAsync(otherRequest, TestContext.Current.CancellationToken);
        using var health = await limited.GetAsync("/api/health", TestContext.Current.CancellationToken);
        using var similarPrefix = await limited.GetAsync("/api/entry-shares-extra", TestContext.Current.CancellationToken);

        // Then
        await AssertRejectedAsync(blocked);
        admitted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        similarPrefix.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private HttpClient CreateClient(string peer) => new(apiFactory.CreateHandler(context =>
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
    }))
    {
        BaseAddress = new Uri("http://localhost"),
    };

    private static HttpRequestMessage Request(int attempt)
    {
        var shareId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var suffix = (attempt % 7) switch
        {
            0 => string.Empty,
            1 => $"/{sessionId}/otp",
            2 => $"/{sessionId}/verify-otp",
            3 => $"/{sessionId}/verify-secret",
            4 => $"/{sessionId}/delivery",
            5 => $"/{sessionId}/confirmation",
            _ => $"/{sessionId}/end",
        };
        return new HttpRequestMessage(HttpMethod.Post, $"/api/entry-shares/{shareId}/sessions{suffix}?attempt={attempt}")
        {
            Content = JsonContent.Create(new
            {
                accessToken = new string('a', 43),
                sessionToken = new string('b', 43),
                generation = 1,
                language = "en",
                code = "123456",
                secret = "fixture-only",
            }),
        };
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Headers.RetryAfter.Delta.ShouldNotBeNull();
        response.Headers.RetryAfter.Delta.Value.TotalSeconds.ShouldBeInRange(1, 60);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

[DisableWafCache]
public sealed class EntrySharingRateLimitApiFactory : ApiFactory
{
    protected override void ConfigureApp(IWebHostBuilder builder)
    {
        base.ConfigureApp(builder);
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Hangfire:Enabled", "false");
        builder.ConfigureAppConfiguration((_, configuration) => configuration
            .AddJsonFile("appsettings.Testing.json", optional: false)
            .AddEnvironmentVariables());
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class EntrySharingRateLimitCollection : ICollectionFixture<EntrySharingRateLimitApiFactory>;
