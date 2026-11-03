using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using OtpNet;
using Palladin.Tests.Integrations.Features.Vault;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<EntrySharingRateLimitCollection>]
public sealed class TotpRateLimitTests(EntrySharingRateLimitApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_RouteSpellingsVary_Then_TotpCredentialChecksShareOnePerAccountBudget()
    {
        // Given
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var authHash = KeyGeneration.GenerateRandomKey(32);
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            authHash, email: $"totp-limit-{Guid.NewGuid():N}@example.com", emailVerified: true);
        using var client = apiFactory.CreateAuthenticatedClient(user);
        var routes = new[]
        {
            "api/auth/totp/enroll", "API/Auth/TOTP/Enroll", "api/auth/totp/enroll/",
            "api/auth/totp/confirm/", "API/AUTH/TOTP/DISABLE",
        };
        foreach (var route in routes)
        {
            using var admitted = await client.PostAsJsonAsync(route, Body(authHash), TestContext.Current.CancellationToken);
            admitted.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, route);
            admitted.StatusCode.ShouldNotBe(HttpStatusCode.NotFound, route);
        }

        // When
        using var rejected = await client.PostAsJsonAsync("api/auth/totp/disable", Body(authHash), TestContext.Current.CancellationToken);

        // Then
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
    }

    private static object Body(byte[] authHash) =>
        new { currentAuthCredential = Base64Url.EncodeToString(authHash), code = "000000" };
}
