using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Triggers;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class NotificationUserPermissionTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserUpserted_Then_SnapshotPermissionsAndSelfScopePersist()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // When
        await Consume(new UserUpsertedEvent(
            userId, organizationId, "Ada", "ada@test.io", Permission.AgentManage, Now()));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var permissions = await readContext.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Permissions)
            .FirstAsync();
        permissions.ShouldBe(Permission.AgentManage);

        var scopes = await readContext.Scopes
            .Where(s => s.OrganizationId == organizationId && s.UserId == userId)
            .Select(s => new { s.Type, s.ItemId })
            .ToListAsync();
        scopes.ShouldContain(s => s.Type == NotificationScopeTypes.User && s.ItemId == userId);
        scopes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_PermissionsChangeWithSameUpdatedAt_Then_SnapshotStillRefreshes()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var t0 = Now();
        await Consume(new UserUpsertedEvent(userId, organizationId, "Ada", "ada@test.io",
            Permission.AgentManage | Permission.GrantManage, t0));

        // When
        await Consume(new UserUpsertedEvent(userId, organizationId, "Ada", "ada@test.io",
            Permission.AgentManage, t0));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var permissions = await readContext.Users
            .Where(u => u.UserId == userId).Select(u => u.Permissions).FirstAsync();
        permissions.ShouldBe(Permission.AgentManage);
    }

    [Fact]
    public async Task When_PermissionRemoved_Then_GatedInboxItemsCascadeDeleteButOthersSurvive()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Consume(new UserUpsertedEvent(
            userId, organizationId, "Ada", "ada@test.io", Permission.AgentManage, Now()));

        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            Gated(organizationId, NotificationType.AgentPending, Permission.AgentManage, []));
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            Gated(organizationId, NotificationType.GrantApproved, null,
                [new NotificationScope(NotificationScopeTypes.User, userId)]));

        await AssertHasTypesAsync(organizationId, userId,
            [NotificationType.AgentPending, NotificationType.GrantApproved]);

        // When
        await Consume(new UserUpsertedEvent(
            userId, organizationId, "Ada", "ada@test.io", Permission.None, Now()));

        // Then
        await AssertHasTypesAsync(organizationId, userId, [NotificationType.GrantApproved]);
    }

    private async Task AssertHasTypesAsync(Guid organizationId, Guid userId, NotificationType[] expected)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var types = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == userId)
            .Select(i => i.Type)
            .ToListAsync();
        types.ShouldBe(expected, ignoreOrder: true);
    }

    private static BroadcastNotificationCommand Gated(
        Guid organizationId,
        NotificationType type,
        Permission? requiredPermission,
        IReadOnlyList<NotificationScope> scopes) =>
        new(
            OrganizationId: organizationId,
            Type: type,
            Category: type == NotificationType.AgentPending
                ? NotificationCategory.ActionRequired
                : NotificationCategory.Update,
            TitleKey: $"notification.{type}.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = Guid.NewGuid().ToString() },
            Scopes: scopes,
            RequiredPermission: requiredPermission,
            SubjectId: Guid.NewGuid(),
            OccurredAt: Now());

    private Task Consume(UserUpsertedEvent @event) =>
        apiFactory.ConsumeAsync<OnUserUpserted, UserUpsertedEvent>(@event);

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
