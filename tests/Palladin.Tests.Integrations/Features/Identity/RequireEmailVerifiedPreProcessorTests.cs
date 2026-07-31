using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Palladin.Core.Security;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

// Unit-level coverage of the gate decision (the config kill-switch path can't use a second host —
// it would race the shared test DB). End-to-end wiring is covered by EmailGateTests.
public sealed class RequireEmailVerifiedPreProcessorTests
{
    [Fact]
    public async Task When_GateDisabled_Then_UnverifiedIsAllowed()
    {
        var context = Context(gateEnabled: false, emailVerified: false);

        await new RequireEmailVerifiedPreProcessor<EmptyRequest>().PreProcessAsync(context, CancellationToken.None);

        context.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task When_GateEnabled_AndUnverified_Then_Forbidden()
    {
        var context = Context(gateEnabled: true, emailVerified: false);

        await new RequireEmailVerifiedPreProcessor<EmptyRequest>().PreProcessAsync(context, CancellationToken.None);

        context.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task When_GateEnabled_AndVerified_Then_Allowed()
    {
        var context = Context(gateEnabled: true, emailVerified: true);

        await new RequireEmailVerifiedPreProcessor<EmptyRequest>().PreProcessAsync(context, CancellationToken.None);

        context.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    private static IPreProcessorContext<EmptyRequest> Context(bool gateEnabled, bool emailVerified)
    {
        var gate = Substitute.For<IEmailVerificationGate>();
        gate.IsEnabled.Returns(gateEnabled);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(gate).BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim(JwtClaimNames.EmailVerified, emailVerified.ToString().ToLowerInvariant()),
            ], "test")),
        };
        httpContext.Response.Body = new MemoryStream();

        var context = Substitute.For<IPreProcessorContext<EmptyRequest>>();
        context.HttpContext.Returns(httpContext);
        return context;
    }
}
