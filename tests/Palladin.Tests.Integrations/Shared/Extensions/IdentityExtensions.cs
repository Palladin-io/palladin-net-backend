using System.Net.Http.Headers;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Tests.Integrations.Shared.Extensions;

internal static class IdentityExtensions
{
    public static HttpClient CreateAuthenticatedClient(this ApiFactory apiFactory, User user, Permission permissions = (Permission)int.MaxValue, PlanType plan = PlanType.Basic)
    {
        using var scope = apiFactory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var authorizationVersion = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers
            .Where(x => x.OrganizationId == user.OrganizationId && x.UserId == user.Id)
            .Select(x => x.AuthorizationVersion)
            .Single();
        var accessToken = tokenService.GenerateAccessToken(
            user, user.OrganizationId, permissions, plan, authorizationVersion);

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    public static string GenerateAccessToken(this ApiFactory apiFactory, User user, Permission permissions = (Permission)int.MaxValue, PlanType plan = PlanType.Basic)
    {
        using var scope = apiFactory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var authorizationVersion = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers
            .Where(x => x.OrganizationId == user.OrganizationId && x.UserId == user.Id)
            .Select(x => x.AuthorizationVersion)
            .Single();
        return tokenService.GenerateAccessToken(
            user, user.OrganizationId, permissions, plan, authorizationVersion);
    }
}
