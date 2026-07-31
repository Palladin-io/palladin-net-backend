using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Audit.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

[Collection<ApiFactoryCollection>]
public sealed class AuditEndpointTests(ApiFactory apiFactory) : TestBase
{
    private Task SeedGrantApprovedAsync(
        Guid orgId,
        Guid vaultId,
        Guid approver,
        Guid? entryId = null,
        string agentName = "agent",
        string actorName = "actor") =>
        AuditSeeding.SeedGrantApprovedAsync(apiFactory, orgId, vaultId, approver, entryId, agentName, actorName);

    private Task SeedRowAsync(Guid orgId, string eventType, Guid? agentId = null, Guid? userId = null) =>
        AuditSeeding.SeedRowAsync(apiFactory, orgId, eventType, agentId, userId);

    [Fact]
    public async Task When_FromToPassedAsRawQueryParams_Then_InstantBindsAnd200()
    {
        // Given — raw query string exercises FastEndpoints value binding for NodaTime Instant,
        // which the JSON serializer config does not cover (regression: every date filter 400d).
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var plain = await client.GetAsync("api/audit-logs?from=2026-07-02T22:00:00Z&to=2026-07-03T21:59:59Z");
        var fractional = await client.GetAsync("api/audit-logs?from=2026-07-02T22:00:00.000Z");

        // Then
        plain.StatusCode.ShouldBe(HttpStatusCode.OK);
        fractional.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_ListingOrgAuditLogs_Then_ReturnsOwnOrgEntries()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedGrantApprovedAsync(organization.Id, vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(new ListAuditLogsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldContain(i => i.EventType == "grant.approved" && i.VaultId == vault.Id);
    }

    [Fact]
    public async Task When_FilteringByEntryId_Then_ReturnsOnlyThatEntrysLogs()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var targetEntryId = Guid.NewGuid();
        await SeedGrantApprovedAsync(organization.Id, vault.Id, user.Id, targetEntryId);
        await SeedGrantApprovedAsync(organization.Id, vault.Id, user.Id, Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(
                new ListAuditLogsRequest { EntryId = targetEntryId });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldNotBeEmpty();
        result.Items.ShouldAllBe(i => i.EntryId == targetEntryId);
    }

    [Fact]
    public async Task When_FilteringByUserId_Then_ReturnsOnlyThatUsersActions()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var targetUserId = Guid.NewGuid();
        await SeedGrantApprovedAsync(organization.Id, vault.Id, targetUserId);
        await SeedGrantApprovedAsync(organization.Id, vault.Id, Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(
                new ListAuditLogsRequest { UserId = targetUserId.ToString() });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldNotBeEmpty();
        result.Items.ShouldAllBe(i => i.UserId == targetUserId);
    }

    [Fact]
    public async Task When_FilteringByMultipleAgents_Then_ReturnsAllMatching_NotOthers()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var agentC = Guid.NewGuid();
        await SeedRowAsync(organization.Id, "credential.accessed", agentId: agentA);
        await SeedRowAsync(organization.Id, "credential.accessed", agentId: agentB);
        await SeedRowAsync(organization.Id, "credential.accessed", agentId: agentC);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(
                new ListAuditLogsRequest { AgentId = $"{agentA},{agentB}" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldContain(i => i.AgentId == agentA);
        result.Items.ShouldContain(i => i.AgentId == agentB);
        result.Items.ShouldNotContain(i => i.AgentId == agentC);
    }

    [Fact]
    public async Task When_FilteringByMultipleEventTypes_Then_ReturnsAllMatching_NotOthers()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await SeedRowAsync(organization.Id, "vault.created", userId: Guid.NewGuid());
        await SeedRowAsync(organization.Id, "org.created", userId: Guid.NewGuid());
        await SeedRowAsync(organization.Id, "entry.created", userId: Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(
                new ListAuditLogsRequest { EventType = "vault.created,org.created" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldContain(i => i.EventType == "vault.created");
        result.Items.ShouldContain(i => i.EventType == "org.created");
        result.Items.ShouldNotContain(i => i.EventType == "entry.created");
    }

    [Fact]
    public async Task When_FilteringBySingleAgent_Then_StillWorks()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agentA = Guid.NewGuid();
        await SeedRowAsync(organization.Id, "credential.accessed", agentId: agentA);
        await SeedRowAsync(organization.Id, "credential.accessed", agentId: Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(
                new ListAuditLogsRequest { AgentId = agentA.ToString() });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.ShouldNotBeEmpty();
        result.Items.ShouldAllBe(i => i.AgentId == agentA);
    }

    [Fact]
    public async Task When_ListingOrgAuditLogs_Then_ReturnsDenormalizedAgentAndActorNames()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedGrantApprovedAsync(organization.Id, vault.Id, user.Id, agentName: "Claude", actorName: "Alice");
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(new ListAuditLogsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = result!.Items.ShouldHaveSingleItem();
        item.AgentName.ShouldBe("Claude");
        item.ActorName.ShouldBe("Alice");
    }

    [Fact]
    public async Task When_ListingOrgAuditLogs_WithoutAuditViewPermission_Then_Returns403()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultManage);

        // When
        var (response, _) = await client
            .GETAsync<ListAuditLogsEndpoint, ListAuditLogsRequest, ListAuditLogsResponse>(new ListAuditLogsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_VaultMemberListsVaultAuditLogs_Then_ReturnsVaultEntries()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedGrantApprovedAsync(organization.Id, vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync($"api/vaults/{vault.Id}/audit-logs");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("grant.approved");
    }

    [Fact]
    public async Task When_NonMemberListsVaultAuditLogs_Then_Returns403()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(outsider);

        // When
        var response = await client.GetAsync($"api/vaults/{vault.Id}/audit-logs");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_ListingAuditLogsForMissingVault_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync($"api/vaults/{Guid.NewGuid()}/audit-logs");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
