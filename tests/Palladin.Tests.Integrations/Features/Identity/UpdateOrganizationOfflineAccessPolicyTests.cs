using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class UpdateOrganizationOfflineAccessPolicyTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(OrganizationOfflineAccessPolicy.Disabled, 2u)]
    [InlineData(OrganizationOfflineAccessPolicy.OneHour, 2u)]
    [InlineData(OrganizationOfflineAccessPolicy.FourHours, 2u)]
    [InlineData(OrganizationOfflineAccessPolicy.TwentyFourHours, 1u)]
    public async Task When_ManagerSelectsFrozenPolicy_Then_ItIsPersistedIdempotently(
        OrganizationOfflineAccessPolicy policy,
        uint expectedVersion)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.OrganizationManagement);
        var request = new UpdateOrganizationOfflineAccessPolicyRequest { Policy = policy };

        var (response, result) = await client.PUTAsync<
            UpdateOrganizationOfflineAccessPolicyEndpoint,
            UpdateOrganizationOfflineAccessPolicyRequest,
            UpdateOrganizationOfflineAccessPolicyResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Policy.ShouldBe(policy);
        result.PolicyVersion.ShouldBe(expectedVersion);
        var retryClient = apiFactory.CreateAuthenticatedClient(user, Permission.OrganizationManagement);
        var (_, retry) = await retryClient.PUTAsync<
            UpdateOrganizationOfflineAccessPolicyEndpoint,
            UpdateOrganizationOfflineAccessPolicyRequest,
            UpdateOrganizationOfflineAccessPolicyResponse>(request);
        retry!.Policy.ShouldBe(policy);
        retry.PolicyVersion.ShouldBe(expectedVersion);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Organizations.SingleAsync(x => x.Id == organization.Id,
                TestContext.Current.CancellationToken);
        persisted.OfflineAccessPolicy.ShouldBe(policy);
        persisted.OfflineAccessPolicyVersion.ShouldBe(expectedVersion);
    }

    [Fact]
    public async Task When_CallerLacksOrganizationManagement_Then_PolicyIsNotChanged()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        var response = await client.PutAsJsonAsync(
            "api/org/offline-access-policy",
            new UpdateOrganizationOfflineAccessPolicyRequest
            {
                Policy = OrganizationOfflineAccessPolicy.Disabled,
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Organizations.SingleAsync(x => x.Id == organization.Id,
                TestContext.Current.CancellationToken);
        persisted.OfflineAccessPolicy.ShouldBe(OrganizationOfflineAccessPolicy.TwentyFourHours);
        persisted.OfflineAccessPolicyVersion.ShouldBe(1u);
    }

    [Fact]
    public async Task When_PolicyIsMissingOrUnknown_Then_RequestIsRejected()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.OrganizationManagement);

        var missing = await client.PutAsJsonAsync(
            "api/org/offline-access-policy",
            new UpdateOrganizationOfflineAccessPolicyRequest(),
            TestContext.Current.CancellationToken);
        var unknown = await client.PutAsJsonAsync(
            "api/org/offline-access-policy",
            new UpdateOrganizationOfflineAccessPolicyRequest
            {
                Policy = (OrganizationOfflineAccessPolicy)999,
            },
            TestContext.Current.CancellationToken);

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
