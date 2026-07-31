using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class GrantPendingManagerRecipientsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_GrantRequested_Then_OnlyVaultMembersWithGrantManageReceive()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var manager = Guid.NewGuid();
        var plainMember = Guid.NewGuid();
        var nonMemberAdmin = Guid.NewGuid();

        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, manager);
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, plainMember);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, manager, Permission.GrantManage);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, plainMember, Permission.AuditView);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, nonMemberAdmin, Permission.GrantManage);

        var grantRequested = new GrantRequestedEvent(
            GrantId: Guid.NewGuid(),
            VaultId: vaultId,
            OrganizationId: organizationId,
            AgentId: Guid.NewGuid(),
            EntryId: Guid.NewGuid(),
            AgentName: "deploy-bot",
            EntryLabel: "GitHub token",
            VaultName: "Prod",
            RequestedMethods: GrantMethods.Get,
            UpdatedAt: Now());

        // When
        var command = await CaptureAsync(grantRequested);
        command.ShouldNotBeNull();
        command.Scopes.ShouldBe([new NotificationScope(NotificationScopeTypes.Vault, vaultId)]);
        command.RequiredPermission.ShouldBe(Permission.GrantManage);
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

        // Then
        await using var verify = apiFactory.Services.CreateAsyncScope();
        var readContext = verify.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var recipients = await readContext.InboxItems
            .Where(i => i.OrganizationId == organizationId && i.Type == NotificationType.GrantPending)
            .Select(i => i.UserId)
            .ToListAsync();
        recipients.ShouldBe([manager]);
        recipients.ShouldNotContain(plainMember);
        recipients.ShouldNotContain(nonMemberAdmin);
    }

    private async Task<BroadcastNotificationCommand?> CaptureAsync(GrantRequestedEvent grantRequested)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        var consumer = new OnGrantRequestedBroadcast(publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(grantRequested));
        return captured;
    }

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
