using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class UpdateOrgTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserHasOrgManagePermission_Then_UpdatesNameAndReturns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<UpdateOrgEndpoint, UpdateOrgRequest>(
            new UpdateOrgRequest { Name = "Renamed Org" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persisted = await readContext.Organizations.FirstOrDefaultAsync(o => o.Id == organization.Id);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("Renamed Org");
    }

    [Fact]
    public async Task When_UserLacksOrgManagePermission_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var response = await client.PUTAsync<UpdateOrgEndpoint, UpdateOrgRequest>(
            new UpdateOrgRequest { Name = "Hacked Org" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
