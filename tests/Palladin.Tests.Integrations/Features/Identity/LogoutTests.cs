using System.Net;
using System.Net.Http.Json;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class LogoutTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_LogsOut_Then_RevokesToken()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var rawToken = Convert.ToBase64String(new byte[] { 99, 98, 97, 96, 95, 94, 93, 92, 91, 90, 89, 88, 87, 86, 85, 84, 83, 82, 81, 80, 79, 78, 77, 76, 75, 74, 73, 72, 71, 70, 69, 68 });
        await apiFactory.Services.SeedRefreshTokenAsync(user.Id, rawToken);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PostAsJsonAsync("api/auth/logout", new { RefreshToken = rawToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var tokenHash = TokenService.HashToken(rawToken);
        var revokedToken = await readContext.RefreshTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash);
        revokedToken.ShouldNotBeNull();
        revokedToken.IsRevoked.ShouldBeTrue();
    }
}
