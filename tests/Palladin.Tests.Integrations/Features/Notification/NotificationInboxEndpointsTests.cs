using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class NotificationInboxEndpointsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ListingInbox_Then_UserSeesOnlyOwnOrganizationsNotifications()
    {
        // Given
        var (userA, orgA, _) = await apiFactory.Services.SeedUserAsync();
        var vaultA = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(orgA.Id, vaultA, userA.Id);
        await DeliverGrantPendingAsync(orgA.Id, vaultA, subjectId: subjectId);

        var orgB = Guid.NewGuid();
        var vaultB = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(orgB, vaultB, userB);
        await DeliverGrantPendingAsync(orgB, vaultB);

        var client = apiFactory.CreateAuthenticatedClient(userA);

        // When
        var (response, result) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Items.Count.ShouldBe(1);
        result.Items[0].Type.ShouldBe(NotificationType.GrantPending);
        result.Items[0].SubjectId.ShouldBe(subjectId);
        result.Items[0].ActionState.ShouldBe("pending");
    }

    [Fact]
    public async Task When_UserStillHasInboxItemButNoScopeRow_Then_ReadStillShowsIt()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await DeliverGrantPendingAsync(org.Id, vaultId);

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();
            await writeContext.Scopes
                .Where(s => s.OrganizationId == org.Id && s.UserId == user.Id
                            && s.Type == NotificationScopeTypes.Vault && s.ItemId == vaultId)
                .ExecuteDeleteAsync();
        }

        // When
        var (_, result) = await apiFactory.CreateAuthenticatedClient(user)
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());

        // Then
        result!.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_UserLosesScopeViaCascade_Then_ListAndSummaryExcludeThatVaultsItems()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await DeliverGrantPendingAsync(org.Id, vaultId);

        var client = apiFactory.CreateAuthenticatedClient(user);
        var (_, before) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        before!.Items.Count.ShouldBe(1);

        // When
        await apiFactory.ConsumeAsync<UpdateUserScopeConsumer, UpdateUserScope>(
            new UpdateUserScope(org.Id, user.Id, NotificationScopeTypes.Vault, vaultId, ScopeAction.Delete));

        // Then
        var (_, after) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        after!.Items.ShouldBeEmpty();

        var (_, summary) = await client
            .GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.UnreadCount.ShouldBe(0);
        summary.PendingActionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_MarkingRead_Then_ItemBecomesReadAndUnreadCountDrops()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await DeliverGrantPendingAsync(org.Id, vaultId);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var (_, list) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        var id = list!.Items[0].Id;

        // When
        var readResponse = await client.PutAsync($"api/notifications/{id}/read", null);

        // Then
        readResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var (_, summary) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.UnreadCount.ShouldBe(0);

        // And
        var secondResponse = await client.PutAsync($"api/notifications/{id}/read", null);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var (_, summaryAfter) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryAfter!.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_MarkingReadUnknownId_Then_NotFound()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PutAsync($"api/notifications/{Guid.NewGuid()}/read", null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_MarkingAllRead_Then_ReturnsMarkedCountAndClearsUnread()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await DeliverGrantPendingAsync(org.Id, vaultId);
        await DeliverGrantPendingAsync(org.Id, vaultId);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .PUTAsync<MarkAllNotificationsReadEndpoint, MarkAllNotificationsReadResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.MarkedCount.ShouldBe(2);
        var (_, summary) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_PagingAcrossEqualOccurredAt_Then_CursorWalksWithoutDuplicates()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        var occurredAt = Now();
        for (var i = 0; i < 3; i++)
        {
            await DeliverGrantPendingAsync(org.Id, vaultId, occurredAt);
        }

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (_, page1) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest { Limit = 2 });
        page1!.Items.Count.ShouldBe(2);
        page1.NextCursor.ShouldNotBeNull();

        var (_, page2) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest { Limit = 2, Cursor = page1.NextCursor });

        // Then
        page2!.Items.Count.ShouldBe(1);
        page2.NextCursor.ShouldBeNull();
        var ids = page1.Items.Select(i => i.Id).Concat(page2.Items.Select(i => i.Id)).ToList();
        ids.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task When_GrantApprovedForSubject_Then_GrantPendingCollapsesAndOnlyTerminalRemains()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await apiFactory.Services.SeedUserScopeAsync(org.Id, user.Id);
        var grantId = Guid.NewGuid();
        await DeliverGrantPendingAsync(org.Id, vaultId, subjectId: grantId);
        await DeliverGrantApprovedAsync(org.Id, user.Id, subjectId: grantId);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (_, result) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());

        // Then
        result!.Items.ShouldNotContain(i => i.Type == NotificationType.GrantPending);
        var approvedCard = result.Items.Single(i => i.Type == NotificationType.GrantApproved);
        approvedCard.ActionState.ShouldBeNull();

        // And
        var (_, summary) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.PendingActionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_GrantDeniedForSubject_Then_GrantPendingCollapsesAndDeniedTerminalRemains()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await apiFactory.Services.SeedUserScopeAsync(org.Id, user.Id);
        var grantId = Guid.NewGuid();
        await DeliverGrantPendingAsync(org.Id, vaultId, subjectId: grantId);
        await DeliverGrantDeniedAsync(org.Id, user.Id, grantId);

        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (_, result) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());

        // Then
        result!.Items.ShouldNotContain(i => i.Type == NotificationType.GrantPending);
        result.Items.ShouldContain(i => i.Type == NotificationType.GrantDenied);

        var (_, summary) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.PendingActionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_GrantTerminalArrives_Then_GrantPendingInboxItemIsDeleted()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        var vaultId = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(org.Id, vaultId, user.Id);
        await apiFactory.Services.SeedUserScopeAsync(org.Id, user.Id);
        var grantId = Guid.NewGuid();
        await DeliverGrantPendingAsync(org.Id, vaultId, subjectId: grantId);

        // When
        await DeliverGrantApprovedAsync(org.Id, user.Id, subjectId: grantId);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i =>
            i.OrganizationId == org.Id && i.UserId == user.Id && i.Type == NotificationType.GrantPending))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentApprovedArrives_Then_AgentApprovedVisibleAndAgentPendingCollapses()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedNotificationUserAsync(org.Id, user.Id, Permission.None);
        var agentId = Guid.NewGuid();
        await DeliverAgentPendingAsync(org.Id, agentId);

        var client = apiFactory.CreateAuthenticatedClient(user);
        var (_, summaryBefore) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryBefore!.PendingActionCount.ShouldBe(1);

        // When
        await DeliverAgentApprovedAsync(org.Id, agentId);

        // Then
        var (_, after) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        after!.Items.ShouldNotContain(i => i.Type == NotificationType.AgentPending);
        var approvedCard = after.Items.Single(i => i.Type == NotificationType.AgentApproved);
        approvedCard.Category.ShouldBe(NotificationCategory.Update);
        approvedCard.ActionState.ShouldBeNull();

        var (_, summaryAfter) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryAfter!.PendingActionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_AgentDeniedMarkerArrives_Then_NoVisibleCardAndAgentPendingCollapses()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedNotificationUserAsync(org.Id, user.Id, Permission.None);
        var agentId = Guid.NewGuid();
        await DeliverAgentPendingAsync(org.Id, agentId);

        var client = apiFactory.CreateAuthenticatedClient(user);
        var (_, summaryBefore) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryBefore!.PendingActionCount.ShouldBe(1);

        // When
        await DeliverAgentResolvedAsync(org.Id, agentId);

        // Then
        var (_, after) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        after!.Items.ShouldNotContain(i => i.Type == NotificationType.AgentPending);
        after.Items.ShouldNotContain(i => i.Type == NotificationType.AgentResolved);
        after.Items.ShouldNotContain(i => i.Type == NotificationType.AgentApproved);

        var (_, summaryAfter) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryAfter!.PendingActionCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_PendingAgentDeactivatedViaEndpoint_Then_AgentPendingCollapses()
    {
        // Given
        var (user, org, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAgentsUserAsync(user.Id);
        await apiFactory.Services.SeedNotificationUserAsync(org.Id, user.Id, Permission.None);
        var agent = await apiFactory.Services.SeedAgentAsync(
            org.Id,
            AgentFaker.Create(organizationId: org.Id)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));
        await DeliverAgentPendingAsync(org.Id, agent.Id);

        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AgentManage);
        var (_, summaryBefore) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryBefore!.PendingActionCount.ShouldBe(1);

        // When
        var response = await client.PostAsync($"api/agents/{agent.Id}/deactivate", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        BroadcastNotificationCommand? captured = null;
        var publisher = Substitute.For<IPublishEndpoint>();
        publisher.When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var trigger = new OnAgentDeactivated(
                scope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>(), publisher);
            await trigger.Consume(apiFactory.MockConsumeContext(
                new AgentDeactivatedEvent(agent.Id, org.Id, user.Id, "Operator", agent.Name ?? "Agent", 1, Now())));
        }
        captured.ShouldNotBeNull();
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(captured);

        // Then
        var (_, after) = await client
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        after!.Items.ShouldNotContain(i => i.Type == NotificationType.AgentPending);

        var (_, summaryAfter) = await client.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summaryAfter!.PendingActionCount.ShouldBe(0);
    }

    private Task DeliverAgentPendingAsync(Guid organizationId, Guid agentId) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.AgentPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.agent_pending.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = agentId.ToString() },
            Scopes: [],
            RequiredPermission: null,
            SubjectId: agentId,
            OccurredAt: Now(),
            Collapsible: true));

    private Task DeliverAgentApprovedAsync(Guid organizationId, Guid agentId) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.AgentApproved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.agent_approved.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = agentId.ToString() },
            Scopes: [],
            RequiredPermission: null,
            SubjectId: agentId,
            OccurredAt: Now(),
            CollapsesPending: true));

    private Task DeliverAgentResolvedAsync(Guid organizationId, Guid agentId) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.AgentResolved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.agent_resolved.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = agentId.ToString() },
            Scopes: [],
            RequiredPermission: null,
            SubjectId: agentId,
            OccurredAt: Now(),
            CollapsesPending: true));

    private Task DeliverGrantPendingAsync(
        Guid organizationId, Guid vaultId, Instant? occurredAt = null, Guid? subjectId = null) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.GrantPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.grant_pending.title",
            Metadata: new Dictionary<string, string>(),
            Scopes: [new NotificationScope(NotificationScopeTypes.Vault, vaultId)],
            RequiredPermission: null,
            SubjectId: subjectId ?? Guid.NewGuid(),
            OccurredAt: occurredAt ?? Now(),
            Collapsible: true));

    private Task DeliverGrantApprovedAsync(Guid organizationId, Guid approver, Guid subjectId) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.GrantApproved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.grant_approved.title",
            Metadata: new Dictionary<string, string>(),
            Scopes: [new NotificationScope(NotificationScopeTypes.User, approver)],
            RequiredPermission: null,
            SubjectId: subjectId,
            OccurredAt: Now(),
            CollapsesPending: true));

    private Task DeliverGrantDeniedAsync(Guid organizationId, Guid denier, Guid subjectId) =>
        Broadcast(new BroadcastNotificationCommand(
            OrganizationId: organizationId,
            Type: NotificationType.GrantDenied,
            Category: NotificationCategory.Update,
            TitleKey: "notification.grant_denied.title",
            Metadata: new Dictionary<string, string>(),
            Scopes: [new NotificationScope(NotificationScopeTypes.User, denier)],
            RequiredPermission: null,
            SubjectId: subjectId,
            OccurredAt: Now(),
            CollapsesPending: true));

    private Task Broadcast(BroadcastNotificationCommand command) =>
        apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
