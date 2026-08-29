using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class GetOrgTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_Then_ReturnsOrgDetails()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client.GETAsync<GetOrgEndpoint, GetOrgResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.OrgId.ShouldBe(organization.Id);
        result.Name.ShouldBe(organization.Name);
        result.MemberCount.ShouldBe(1);
        result.SeatUsage.ShouldBe(1);
        result.SeatLimit.ShouldBe(1);
        result.OfflineAccessPolicy.ShouldBe(OrganizationOfflineAccessPolicy.TwentyFourHours);
        result.OfflineAccessPolicyVersion.ShouldBe(1u);
    }

    [Fact]
    public async Task When_Unauthenticated_Then_Returns401()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var response = await client.GetAsync("api/org");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
