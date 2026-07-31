using System.Net;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

// Gate ON (Testing default). GET api/vaults stands in for any sensitive user-JWT endpoint.
[Collection<ApiFactoryCollection>]
public sealed class EmailGateTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UnverifiedUser_HitsSensitiveEndpoint_Then_Returns403()
    {
        // Given
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], emailVerified: false);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/vaults", TestContext.Current.CancellationToken);

        // Then — distinct error key so the client can target only the verification case
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("email-not-verified");
    }

    [Fact]
    public async Task When_VerifiedUser_HitsSensitiveEndpoint_Then_Allowed()
    {
        // Given — a verified user (OAuth users carry the same email_verified=true claim)
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], emailVerified: true);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/vaults", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
