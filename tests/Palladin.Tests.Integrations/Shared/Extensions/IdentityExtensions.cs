using System.Net.Http.Headers;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Tests.Integrations.Shared.Extensions;

internal static class IdentityExtensions
{
    public static HttpClient CreateAuthenticatedClient(this ApiFactory apiFactory, User user, Permission permissions = (Permission)int.MaxValue, PlanType plan = PlanType.Basic)
    {
        using var scope = apiFactory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var accessToken = tokenService.GenerateAccessToken(user, user.OrganizationId, permissions, plan);

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    public static string GenerateAccessToken(this ApiFactory apiFactory, User user, Permission permissions = (Permission)int.MaxValue, PlanType plan = PlanType.Basic)
    {
        using var scope = apiFactory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return tokenService.GenerateAccessToken(user, user.OrganizationId, permissions, plan);
    }
}
