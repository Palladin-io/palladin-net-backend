using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.Push;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Palladin.Module.Notification.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class BroadcastNotificationConsumerTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_VaultNotificationContainsPresentationMetadata_Then_FailsClosed()
    {
        var command = GrantPending(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Metadata = new Dictionary<string, string> { ["entryLabel"] = "Production database" },
        };

        await Should.ThrowAsync<InvalidOperationException>(() =>
            apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command));
    }

    [Fact]
    public async Task When_GrantApprovalRequested_Then_SendsSignalRAndPushToManager()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var manager = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, manager);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, manager, Permission.GrantManage);
        var web = Substitute.For<IWebNotifier>();
        var push = Substitute.For<IPushNotificationService>();

        // When
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<BroadcastNotificationConsumer>(
            scope.ServiceProvider, web, push);
        await consumer.Consume(apiFactory.MockConsumeContext(GrantPending(organizationId, vaultId)));

        // Then
        await web.Received(1).NotifyUsersAsync(
            organizationId,
            Arg.Is<IReadOnlyCollection<Guid>>(users => users.SequenceEqual(new[] { manager })),
            Arg.Any<NotificationPayload>(),
            Arg.Any<CancellationToken>());
        await push.Received(1).SendToUsersAsync(
            organizationId,
            Arg.Is<IReadOnlyCollection<Guid>>(users => users.SequenceEqual(new[] { manager })),
            Arg.Any<PushDispatch>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_BroadcastToVaultScope_Then_OnlyUsersWithThatScopeGetInboxItems()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var recipients = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId)
            .Select(i => i.UserId)
            .ToListAsync();
        recipients.ShouldBe([member]);
        recipients.ShouldNotContain(outsider);
    }

    [Fact]
    public async Task When_DuplicateOccurrenceConsumed_Then_NoDuplicateInboxItems()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        var command = GrantPending(organizationId, vaultId);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var count = await readContext.InboxItems
            .CountAsync(i => i.OrganizationId == organizationId
                             && i.Type == command.Type
                             && i.SubjectId == command.SubjectId);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task When_DistinctOccurrencesSameTypeConsumed_Then_NotDeduped()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var count = await readContext.InboxItems
            .CountAsync(i => i.OrganizationId == organizationId
                             && i.UserId == member
                             && i.Type == NotificationType.GrantPending);
        count.ShouldBe(2);
    }

    [Fact]
    public async Task When_BroadcastToUserSelfScope_Then_OnlyThatUserGetsInboxItem()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var approver = Guid.NewGuid();
        await apiFactory.Services.SeedUserScopeAsync(organizationId, approver);

        var command = GrantApproved(organizationId, approver);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var items = await readContext.InboxItems.Where(i => i.OrganizationId == organizationId).ToListAsync();
        items.Select(i => i.UserId).ShouldBe([approver]);
    }

    [Fact]
    public async Task When_InboxDisabledForNonMandatoryType_Then_NoInboxItemWritten()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        await apiFactory.Services.SeedPreferenceAsync(
            organizationId, member, NotificationType.CredentialStale,
            inboxEnabled: false, signalREnabled: false, pushEnabled: false);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            CredentialStale(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == member))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_MandatoryTypeWithInboxDisabledPref_Then_InboxItemStillWritten()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        await apiFactory.Services.SeedPreferenceAsync(
            organizationId, member, NotificationType.GrantPending,
            inboxEnabled: false, signalREnabled: false, pushEnabled: false);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == member))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_TerminalApprovedConsumed_Then_PendingCollapsesButTerminalSurvives()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedUserScopeAsync(organizationId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId) with { SubjectId = grantId });

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantApproved(organizationId, member) with { SubjectId = grantId });

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var mine = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == member)
            .Select(i => i.Type)
            .ToListAsync();
        mine.ShouldNotContain(NotificationType.GrantPending);
        mine.ShouldContain(NotificationType.GrantApproved);
    }

    [Fact]
    public async Task When_PendingArrivesAfterTerminal_Then_TerminalRedeliveryStillCollapsesIt()
    {
        // Given — model the out-of-order delivery seen when the terminal saga completes quickly.
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedUserScopeAsync(organizationId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        var terminal = GrantApproved(organizationId, member) with { SubjectId = grantId };
        var pending = GrantPending(organizationId, vaultId) with { SubjectId = grantId };
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(terminal);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(pending);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(terminal);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var types = await scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>()
            .InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == member && i.SubjectId == grantId)
            .Select(i => i.Type)
            .ToListAsync();
        types.ShouldBe([NotificationType.GrantApproved]);
    }

    [Fact]
    public async Task When_TerminalConsumed_Then_DoesNotCollapsePendingOfAnotherSubject()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var otherGrantId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedUserScopeAsync(organizationId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId) with { SubjectId = otherGrantId });

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantApproved(organizationId, member) with { SubjectId = Guid.NewGuid() });

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var mine = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == member)
            .Select(i => i.Type)
            .ToListAsync();
        mine.ShouldContain(NotificationType.GrantPending);
    }

    [Fact]
    public async Task When_TerminalConsumed_Then_DoesNotCollapseNonCollapsibleOfSameSubject()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedUserScopeAsync(organizationId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            CredentialStale(organizationId, vaultId) with { SubjectId = entryId });

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantApproved(organizationId, member) with { SubjectId = entryId });

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var mine = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.UserId == member)
            .Select(i => i.Type)
            .ToListAsync();
        mine.ShouldContain(NotificationType.CredentialStale);
        mine.ShouldContain(NotificationType.GrantApproved);
    }

    [Fact]
    public async Task When_InvisibleAgentResolvedMarkerConsumed_Then_PendingCollapsesAndNoCardWritten()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var manager = Guid.NewGuid();
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, manager, Permission.AgentManage);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            AgentPending(organizationId, agentId));

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            AgentResolved(organizationId, agentId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var types = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId)
            .Select(i => i.Type)
            .ToListAsync();
        types.ShouldNotContain(NotificationType.AgentPending);
        types.ShouldNotContain(NotificationType.AgentResolved);
    }

    [Fact]
    public async Task When_InScopeButWithoutRequiredPermission_Then_DoesNotReceive()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.AuditView);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == member))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_HasRequiredPermissionButOutsideScope_Then_DoesNotReceive()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var user = Guid.NewGuid();
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, user, Permission.GrantManage);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == user))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task When_BothScopeAndPermission_Then_Receives()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var manager = Guid.NewGuid();
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, manager, Permission.AgentManage);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            AgentPending(organizationId, Guid.NewGuid()));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i =>
            i.OrganizationId == organizationId && i.UserId == manager && i.Type == NotificationType.AgentPending))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_NullRequiredPermission_Then_DeliveredRegardlessOfPermissionBits()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var approver = Guid.NewGuid();
        await apiFactory.Services.SeedUserScopeAsync(organizationId, approver);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, approver, Permission.None);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantApproved(organizationId, approver));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i =>
            i.OrganizationId == organizationId && i.UserId == approver && i.Type == NotificationType.GrantApproved))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_NullRequiredPermissionAndNoSnapshotRow_Then_StillDelivered()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var approver = Guid.NewGuid();
        await apiFactory.Services.SeedUserScopeAsync(organizationId, approver);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantApproved(organizationId, approver));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == approver))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_GatedButNoSnapshotRow_Then_Excluded()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(
            GrantPending(organizationId, vaultId));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i => i.OrganizationId == organizationId && i.UserId == member))
            .ShouldBeFalse();
    }

    private static BroadcastNotificationCommand GrantPending(Guid organizationId, Guid vaultId) =>
        new(
            OrganizationId: organizationId,
            Type: NotificationType.GrantPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.grant_pending.title",
            Metadata: new Dictionary<string, string>(),
            Scopes: [new NotificationScope(NotificationScopeTypes.Vault, vaultId)],
            RequiredPermission: Permission.GrantManage,
            SubjectId: Guid.NewGuid(),
            OccurredAt: Now(),
            Collapsible: true);

    private static BroadcastNotificationCommand GrantApproved(Guid organizationId, Guid approver) =>
        new(
            OrganizationId: organizationId,
            Type: NotificationType.GrantApproved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.grant_approved.title",
            Metadata: new Dictionary<string, string> { ["grantId"] = Guid.NewGuid().ToString() },
            Scopes: [new NotificationScope(NotificationScopeTypes.User, approver)],
            RequiredPermission: null,
            SubjectId: Guid.NewGuid(),
            OccurredAt: Now(),
            CollapsesPending: true);

    private static BroadcastNotificationCommand CredentialStale(Guid organizationId, Guid vaultId) =>
        new(
            OrganizationId: organizationId,
            Type: NotificationType.CredentialStale,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.credential_stale.title",
            Metadata: new Dictionary<string, string>(),
            Scopes: [new NotificationScope(NotificationScopeTypes.Vault, vaultId)],
            RequiredPermission: Permission.GrantManage,
            SubjectId: Guid.NewGuid(),
            OccurredAt: Now());

    private static BroadcastNotificationCommand AgentPending(Guid organizationId, Guid agentId) =>
        new(
            OrganizationId: organizationId,
            Type: NotificationType.AgentPending,
            Category: NotificationCategory.ActionRequired,
            TitleKey: "notification.agent_pending.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = agentId.ToString() },
            Scopes: [],
            RequiredPermission: Permission.AgentManage,
            SubjectId: agentId,
            OccurredAt: Now(),
            Collapsible: true);

    private static BroadcastNotificationCommand AgentResolved(Guid organizationId, Guid agentId) =>
        new(
            OrganizationId: organizationId,
            Type: NotificationType.AgentResolved,
            Category: NotificationCategory.Update,
            TitleKey: "notification.agent_resolved.title",
            Metadata: new Dictionary<string, string> { ["agentId"] = agentId.ToString() },
            Scopes: [],
            RequiredPermission: null,
            SubjectId: agentId,
            OccurredAt: Now(),
            CollapsesPending: true);

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
