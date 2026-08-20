using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Palladin.Module.Notification.Shared;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class VaultSyncInvalidationTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task Invalidation_NotifiesExactCommittedMemberSnapshot_WithoutInboxWrite()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var firstMember = Guid.NewGuid();
        var secondMember = Guid.NewGuid();
        var notifier = Substitute.For<IRealtimeEventNotifier>();
        var command = Changed(organizationId, vaultId) with
        {
            RecipientUserIds = [firstMember, secondMember, firstMember],
        };

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<BroadcastVaultSyncInvalidationConsumer>(
            scope.ServiceProvider,
            notifier);
        await consumer.Consume(apiFactory.MockConsumeContext(command));

        await notifier.Received(1).NotifyVaultSyncInvalidationAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids =>
                ids.Count == 2 && ids.Contains(firstMember) && ids.Contains(secondMember)),
            Arg.Is<VaultSyncInvalidationPayload>(payload =>
                payload.ProtocolVersion == 1
                && payload.VaultId == vaultId
                && payload.MemberSequence == "12"
                && payload.MutationVersion == "24"
                && !payload.Removed),
            Arg.Any<CancellationToken>());
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(item => item.OrganizationId == organizationId)).ShouldBeFalse();
    }

    [Fact]
    public async Task RemovedInvalidation_UsesExplicitFormerMemberRecipient()
    {
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var formerMember = Guid.NewGuid();
        var notifier = Substitute.For<IRealtimeEventNotifier>();
        var command = Changed(organizationId, vaultId) with
        {
            Removed = true,
            RecipientUserIds = [formerMember],
        };

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<BroadcastVaultSyncInvalidationConsumer>(
            scope.ServiceProvider,
            notifier);
        await consumer.Consume(apiFactory.MockConsumeContext(command));

        await notifier.Received(1).NotifyVaultSyncInvalidationAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { formerMember })),
            Arg.Is<VaultSyncInvalidationPayload>(payload => payload.Removed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingRecipients_FailsBeforeSignalRDelivery()
    {
        var notifier = Substitute.For<IRealtimeEventNotifier>();
        var command = Changed(Guid.NewGuid(), Guid.NewGuid()) with
        {
            RecipientUserIds = [],
        };

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<BroadcastVaultSyncInvalidationConsumer>(
            scope.ServiceProvider,
            notifier);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            consumer.Consume(apiFactory.MockConsumeContext(command)));
        await notifier.DidNotReceiveWithAnyArgs().NotifyVaultSyncInvalidationAsync(
            default!, default!, default);
    }

    [Fact]
    public async Task VaultEvent_MapsCanonicalMonotonicWire()
    {
        var publish = Substitute.For<IPublishEndpoint>();
        var consumer = new OnVaultSyncInvalidated(publish);
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var memberIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var occurredAt = Instant.FromUnixTimeSeconds(123);

        await consumer.Consume(apiFactory.MockConsumeContext(
            new VaultSyncInvalidatedEvent(organizationId, vaultId, memberIds, 12, 24, occurredAt)));

        await publish.Received(1).Publish(
            Arg.Is<BroadcastVaultSyncInvalidationCommand>(command =>
                command.OrganizationId == organizationId
                && command.VaultId == vaultId
                && command.MemberSequence == "12"
                && command.MutationVersion == "24"
                && !command.Removed
                && command.RecipientUserIds.SequenceEqual(memberIds)),
            Arg.Any<CancellationToken>());
    }

    private static BroadcastVaultSyncInvalidationCommand Changed(Guid organizationId, Guid vaultId) =>
        new(
            organizationId,
            vaultId,
            "12",
            "24",
            false,
            [Guid.NewGuid()],
            Instant.FromUnixTimeSeconds(123));
}
