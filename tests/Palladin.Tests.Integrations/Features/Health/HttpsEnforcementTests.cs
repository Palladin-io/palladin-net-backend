using System.Net;
using Shouldly;
using Palladin.Api.Middlewares;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Tests.Integrations.Features.Health;

public sealed class HttpsEnforcementTests
{
    [Theory]
    [InlineData("http", false, false, false, HttpStatusCode.Forbidden)]
    [InlineData("https", false, false, false, HttpStatusCode.NoContent)]
    [InlineData("http", true, true, false, HttpStatusCode.NoContent)]
    [InlineData("http", true, false, false, HttpStatusCode.Forbidden)]
    [InlineData("http", false, false, true, HttpStatusCode.NoContent)]
    public async Task When_HttpsPortIsAbsent_Then_TransportBoundaryIsEnforced(
        string scheme, bool forwardedHttps, bool trustedProxy, bool allowHttp, HttpStatusCode expectedStatus)
    {
        // Given
        var reachedEndpoint = false;
        using var server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddHttpsRedirection(_ => { }))
            .Configure(app =>
            {
                var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
                forwarding.KnownProxies.Clear();
                forwarding.KnownIPNetworks.Clear();
                forwarding.KnownProxies.Add(IPAddress.Parse(trustedProxy ? "192.0.2.10" : "192.0.2.11"));
                app.UseForwardedHeaders(forwarding);
                if (!allowHttp)
                {
                    app.UseHttpsRedirection();
                    app.UseMiddleware<RequireHttpsMiddleware>();
                }
                app.Run(context =>
                {
                    reachedEndpoint = true;
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                });
            }));
        using var client = new HttpClient(server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10")));
        if (forwardedHttps)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        }

        // When
        using var response = await client.GetAsync($"{scheme}://api.example.test/api/protected", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(expectedStatus);
        reachedEndpoint.ShouldBe(expectedStatus == HttpStatusCode.NoContent);
    }
}
