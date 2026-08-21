using System.Net;
using FastEndpoints;
using Palladin.Core.Security;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Module.Search.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Search;

[Collection<ApiFactoryCollection>]
public sealed class GlobalSearchTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task Post_ReturnsOnlyAuthorizedAdministrativeResults()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agent = await apiFactory.Services.SeedSearchAgentAsync(organization.Id, "Prod Deploy Bot zqx");
        var member = await apiFactory.Services.SeedSearchMemberAsync(
            organization.Id, "Operations", "zqx-member@example.test");

        var (response, result) = await SearchAsync(
            apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage), "zqx");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Results.Select(x => x.Id).ShouldBe([agent.Id, member.Id], ignoreOrder: true);
        result.Results.ShouldAllBe(x => x.Type == "agent" || x.Type == "member");
    }

    [Fact]
    public async Task Post_HidesAgentWithoutAgentManage_ButKeepsMember()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedSearchAgentAsync(organization.Id, "Bot zqx");
        var member = await apiFactory.Services.SeedSearchMemberAsync(
            organization.Id, "Member zqx", "member@example.test");

        var (_, result) = await SearchAsync(
            apiFactory.CreateAuthenticatedClient(user, Permission.ReadApiKey), "zqx");

        var item = result!.Results.ShouldHaveSingleItem();
        item.Id.ShouldBe(member.Id);
        item.Type.ShouldBe("member");
    }

    [Fact]
    public async Task Post_IsTenantIsolated()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedSearchMemberAsync(
            otherOrganization.Id, "Foreign zqx", "foreign@example.test");

        var (_, result) = await SearchAsync(apiFactory.CreateAuthenticatedClient(user), "zqx");

        result!.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_CanMatchMemberEmail_ButNeverReturnsIt()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedSearchMemberAsync(
            organization.Id, "Visible Name", "unique-secret-email@example.test");

        var (_, result) = await SearchAsync(apiFactory.CreateAuthenticatedClient(user), "unique-secret-email");

        var item = result!.Results.ShouldHaveSingleItem();
        item.Id.ShouldBe(member.Id);
        item.Name.ShouldBe("Visible Name");
        typeof(SearchResultItem).GetProperties().Select(x => x.Name)
            .ShouldBe(["Type", "Id", "Name"], ignoreOrder: true);
    }

    [Fact]
    public async Task Post_NeverReturnsRemovalTombstones()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedSearchMemberAsync(
            organization.Id, "Removed zqx", "removed@example.test");
        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(organization.Id, member.Id, SearchItemTypes.Member,
                member.UpdatedAt + NodaTime.Duration.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        var (_, result) = await SearchAsync(apiFactory.CreateAuthenticatedClient(user), "zqx");

        result!.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task LegacyGetWithQueryString_IsRejected()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var response = await apiFactory.CreateAuthenticatedClient(user)
            .GetAsync("api/search?q=must-not-be-accepted");

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Post_ShortQueryReturnsEmpty_AndLimitIsBounded()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        for (var i = 0; i < 30; i++)
        {
            await apiFactory.Services.SeedSearchMemberAsync(
                organization.Id, $"Bounded zqx {i:D2}", $"member{i}@example.test");
        }

        var client = apiFactory.CreateAuthenticatedClient(user);
        (await SearchAsync(client, "z")).Result!.Results.ShouldBeEmpty();
        (await SearchAsync(client, "zqx", 25)).Result!.Results.Count.ShouldBe(25);
    }

    [Fact]
    public async Task Post_TreatsSqlWildcardsLiterally_AndRejectsOversizedQuery()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedSearchMemberAsync(
            organization.Id, "Ordinary Member", "ordinary@example.test");
        var client = apiFactory.CreateAuthenticatedClient(user);

        (await SearchAsync(client, "%%")).Result!.Results.ShouldBeEmpty();
        (await SearchAsync(client, new string('x', 129))).Response.StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<(HttpResponseMessage Response, GlobalSearchResponse? Result)> SearchAsync(
        HttpClient client, string query, int? limit = null)
    {
        var (response, result) = await client
            .POSTAsync<GlobalSearchEndpoint, GlobalSearchRequest, GlobalSearchResponse>(
                new GlobalSearchRequest { Q = query, Limit = limit });
        return (response, result);
    }
}
