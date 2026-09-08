using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Palladin.Api.Framework;
using Shouldly;

namespace Palladin.Tests.Integrations.Framework;

public sealed class AgentPairingRouteTests
{
    [Theory]
    [InlineData("/api/agent-pairings/67ad9d63-f947-4b6c-8f64-e564d42d620f/status")]
    [InlineData("/api/agent-pairings/67ad9d63f9474b6c8f64e564d42d620f/status")]
    [InlineData("/api/agent-pairings/67ad9d63-f947-4b6c-8f64-e564d42d620f/status/")]
    [InlineData("/API/AGENT-PAIRINGS/67AD9D63-F947-4B6C-8F64-E564D42D620F/STATUS")]
    public void IsStatus_AcceptsEveryPairingStatusRouteThatBindsAsGuid(string path) =>
        AgentPairingRoute.IsStatus(new PathString(path)).ShouldBeTrue();

    [Theory]
    [InlineData("/api/agent-pairings")]
    [InlineData("/api/agent-pairings/status")]
    [InlineData("/api/agent-pairings//status")]
    [InlineData("/api/agent-pairings/not-a-guid/status")]
    [InlineData("/api/agent-pairings/67ad9d63-f947-4b6c-8f64-e564d42d620f/approve")]
    [InlineData("/api/agent-pairings/67ad9d63-f947-4b6c-8f64-e564d42d620f/status/extra")]
    public void IsStatus_RejectsOtherRoutes(string path) =>
        AgentPairingRoute.IsStatus(new PathString(path)).ShouldBeFalse();

    [Fact]
    public void When_StatusUsesRouteEquivalentTrailingSlash_Then_SharesTheBoundedPartition()
    {
        // Given
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPalladinRateLimiter();
        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        limiter.ShouldNotBeNull();

        // When
        for (var requestNumber = 0; requestNumber < 120; requestNumber++)
        {
            using var lease = limiter.AttemptAcquire(CreateContext(requestNumber % 2 == 0));
            lease.IsAcquired.ShouldBeTrue();
        }

        // Then
        using var rejected = limiter.AttemptAcquire(CreateContext(trailingSlash: true));
        rejected.IsAcquired.ShouldBeFalse();
    }

    private static HttpContext CreateContext(bool trailingSlash)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.42");
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/agent-pairings/67ad9d63-f947-4b6c-8f64-e564d42d620f/status"
            + (trailingSlash ? "/" : string.Empty);
        return context;
    }
}
