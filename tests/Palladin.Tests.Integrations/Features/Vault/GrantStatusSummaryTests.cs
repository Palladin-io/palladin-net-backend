using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class GrantStatusSummaryTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserHasGrantManage_Then_ReturnsCorrectCountsPerStatus()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);

        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Pending).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Revoked).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Expired).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Consumed).Generate());
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Denied).Generate());

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<GrantStatusSummaryEndpoint, GrantStatusSummaryResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Pending.ShouldBe(1);
        result.Active.ShouldBe(2);
        result.Expired.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        result.Consumed.ShouldBe(1);
        result.Denied.ShouldBe(1);
    }

    [Fact]
    public async Task When_OrgHasNoGrants_Then_ReturnsAllSixStatusesAsZero()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<GrantStatusSummaryEndpoint, GrantStatusSummaryResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.Pending.ShouldBe(0);
        result.Active.ShouldBe(0);
        result.Expired.ShouldBe(0);
        result.Revoked.ShouldBe(0);
        result.Consumed.ShouldBe(0);
        result.Denied.ShouldBe(0);
    }

    [Fact]
    public async Task When_OtherOrgHasGrants_Then_SummaryExcludesOtherOrgCounts()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var agent = await apiFactory.Services.SeedVaultAgentAsync(organization.Id);
        await apiFactory.Services.SeedGranularGrantAsync(
            GrantFaker.CreateGranular(vaultId: vault.Id, organizationId: organization.Id, agentId: agent.Id,
                createdBy: user.Id, status: GrantStatus.Active).Generate());

        var (otherUser, otherOrg, _) = await apiFactory.Services.SeedUserAsync();
        var otherVault = await apiFactory.Services.SeedVaultAsync(otherOrg.Id, otherUser.Id);
        var otherAgent = await apiFactory.Services.SeedVaultAgentAsync(otherOrg.Id);
        for (var i = 0; i < 5; i++)
        {
            await apiFactory.Services.SeedGranularGrantAsync(
                GrantFaker.CreateGranular(vaultId: otherVault.Id, organizationId: otherOrg.Id, agentId: otherAgent.Id,
                    createdBy: otherUser.Id, status: GrantStatus.Active).Generate());
        }

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<GrantStatusSummaryEndpoint, GrantStatusSummaryResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Active.ShouldBe(1);
    }

    [Fact]
    public async Task When_UserMissingGrantManage_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);

        // When
        var (response, _) = await client
            .GETAsync<GrantStatusSummaryEndpoint, GrantStatusSummaryResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
