using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<EmailVerificationGateDisabledCollection>]
public sealed class OAuthAuthenticateGateDisabledTests(EmailVerificationGateDisabledApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_GateDisabledAndUnverifiedAccountHasActivityBeforeOnboardingFlags_Then_Returns401AndKeepsAccount()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"unverified-active-{Guid.NewGuid():N}@example.com";
        var (squatter, squatterOrganization) = await apiFactory.Services.SeedPasswordUserAsync(
            RandomNumberGenerator.GetBytes(32), email: email, emailVerified: false);
        var (apiKey, _) = await apiFactory.Services.SeedApiKeyAsync(squatterOrganization.Id, createdBy: squatter.Id);
        apiFactory.GoogleOAuthProvider.ValidateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalUserInfo($"google-active-{Guid.NewGuid():N}", email, true, "Google Victim", null));

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/oauth/google", new { Token = "valid-google-token" }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await identity.Users.SingleAsync(u => u.Email == email, TestContext.Current.CancellationToken)).Id.ShouldBe(squatter.Id);
        (await identity.Organizations.AnyAsync(o => o.Id == squatterOrganization.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await identity.OAuthConnections.AnyAsync(c => c.UserId == squatter.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>().ApiKeys
            .AnyAsync(k => k.Id == apiKey.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }
}

[DisableWafCache]
public sealed class EmailVerificationGateDisabledApiFactory : ApiFactory
{
    protected override void ConfigureApp(IWebHostBuilder builder)
    {
        base.ConfigureApp(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Modules:Identity:EmailVerification:GateEnabled"] = "false",
                ["Hangfire:Enabled"] = "false",
            }));
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class EmailVerificationGateDisabledCollection : ICollectionFixture<EmailVerificationGateDisabledApiFactory>;
