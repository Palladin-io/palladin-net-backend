using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingActivityTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ExpiryCleanupIsRetried_Then_DeliveryMaterialAndExpiredSessionsAreRemovedButAuditSurvives()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var source = new EntryScope(organization.Id, vault.Id, entryId);
        var expired = CreateShare(Instant.FromUtc(1990, 1, 1, 0, 0), false, source, user.Id);
        var active = CreateShare(apiFactory.FakeClock.GetCurrentInstant(), false, source, user.Id);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            database.EntryShares.AddRange(expired, active);
            database.EntryShareSessions.Add(expired.OpenSession(Guid.NewGuid(), new byte[32], expired.CreatedAt, Duration.FromMinutes(15)));
            database.EntryShareSessions.Add(active.OpenSession(Guid.NewGuid(), new byte[32], active.CreatedAt, Duration.FromMinutes(15)));
            database.EntryShareActivities.AddRange(expired.Activities.Concat(active.Activities));
            await database.SaveChangesAsync();
        }

        // When
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            await new ExpireEntrySharesJob(scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                Options.Create(new ExpireEntrySharesJobOptions { BatchSize = 1, MaximumBatches = 1 }),
                apiFactory.FakeClock).ExecuteAsync();
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var expiredRow = await verification.EntryShares.SingleAsync(x => x.Id == expired.Id);
        expiredRow.ExpiredAt.ShouldNotBeNull();
        expiredRow.Ciphertext.ShouldBeEmpty();
        expiredRow.AccessTokenHash.ShouldBeEmpty();
        (await verification.EntryShareSessions.AnyAsync(x => x.ShareId == expired.Id)).ShouldBeFalse();
        (await verification.EntryShareActivities.CountAsync(x => x.ShareId == expired.Id
            && x.Kind == EntryShareActivityKind.Expired)).ShouldBe(1);
        (await verification.EntryShares.SingleAsync(x => x.Id == active.Id)).Ciphertext.ShouldNotBeEmpty();
        (await verification.EntryShareSessions.CountAsync(x => x.ShareId == active.Id)).ShouldBe(1);
    }

    [Theory]
    [InlineData(EntryShareActivityKind.Created, AuditActorType.User, AuditEventType.EntryShareCreated)]
    [InlineData(EntryShareActivityKind.Delivered, AuditActorType.ExternalRecipient, AuditEventType.EntryShareDelivered)]
    [InlineData(EntryShareActivityKind.Confirmed, AuditActorType.ExternalRecipient, AuditEventType.EntryShareConfirmed)]
    [InlineData(EntryShareActivityKind.ProtectionChanged, AuditActorType.User, AuditEventType.EntryShareProtectionChanged)]
    [InlineData(EntryShareActivityKind.Expired, AuditActorType.System, AuditEventType.EntryShareExpired)]
    [InlineData(EntryShareActivityKind.RevokedBySender, AuditActorType.User, AuditEventType.EntryShareRevoked)]
    [InlineData(EntryShareActivityKind.EndedByRecipient, AuditActorType.ExternalRecipient, AuditEventType.EntryShareEnded)]
    [InlineData(EntryShareActivityKind.SourceAccessRemoved, AuditActorType.System, AuditEventType.EntryShareSourceAccessRemoved)]
    public async Task When_AnActivityIsRedelivered_Then_OneOpaqueAuditRowHasTheCorrectActor(
        EntryShareActivityKind kind, AuditActorType actor, string eventType)
    {
        // Given
        var message = new EntryShareActivityEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), 3, kind, false, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ForwardAsync(message);
        await ForwardAsync(message);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var row = await database.AuditLogEntries.SingleAsync(x => x.OrganizationId == message.OrganizationId);
        row.EventType.ShouldBe(eventType);
        row.ActorType.ShouldBe(actor);
        row.UserId.ShouldBe(actor == AuditActorType.User ? message.SenderId : null);
        row.AgentId.ShouldBeNull();
        row.IpAddress.ShouldBeNull();
        row.AgentName.ShouldBeNull();
        row.ActorName.ShouldBeNull();
        row.HasExplicitOccurrenceId.ShouldBeTrue();
        row.Metadata.ShouldBe(new Dictionary<string, string> { ["shareId"] = message.ShareId.ToString("D"), ["sequence"] = "3" });
        row.Id.ShouldBe(EntryShareActivityIdentity.For(message.ShareId, message.Sequence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_TwoReceiptsAreConfirmedAtTheSameTime_Then_AuditPreservesBothAndInboxHonorsTheShareChoice(bool notify)
    {
        // Given
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var share = CreateShare(now, notify);
        var first = share.OpenSession(Guid.NewGuid(), new byte[32], now, Duration.FromMinutes(15));
        var second = share.OpenSession(Guid.NewGuid(), new byte[32], now, Duration.FromMinutes(15));
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var notification = seedScope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();
            notification.Add(NotificationPreference.Create(share.OrganizationId, share.CreatedBy,
                NotificationType.EntryShareReceived, false, false, false, now));
            await notification.SaveChangesAsync();
        }

        // When
        share.Deliver(first, now);
        share.Deliver(second, now);
        share.ConfirmReceipt(first, now);
        share.ConfirmReceipt(second, now);
        share.ConfirmReceipt(first, now).ShouldBeFalse();
        foreach (var activity in share.Activities.Reverse())
        {
            await ForwardAsync(activity.ToEvent());
            await ForwardAsync(activity.ToEvent());
        }

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var events = await audit.AuditLogEntries.Where(x => x.OrganizationId == share.OrganizationId).ToListAsync();
        events.Count.ShouldBe(5);
        events.Count(x => x.EventType == AuditEventType.EntryShareDelivered).ShouldBe(2);
        events.Count(x => x.EventType == AuditEventType.EntryShareConfirmed).ShouldBe(2);
        var notificationDatabase = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var inbox = await notificationDatabase.InboxItems.Where(x => x.OrganizationId == share.OrganizationId).ToListAsync();
        inbox.Count.ShouldBe(notify ? 1 : 0);
        if (notify)
        {
            var row = inbox.Single();
            row.UserId.ShouldBe(share.CreatedBy);
            row.SubjectId.ShouldBe(share.Id);
            row.Type.ShouldBe(NotificationType.EntryShareReceived);
            row.Metadata.Count.ShouldBe(3);
            row.Metadata["shareId"].ShouldBe(share.Id.ToString("D"));
            row.Metadata["vaultId"].ShouldBe(share.VaultId.ToString("D"));
            row.Metadata["entryId"].ShouldBe(share.EntryId.ToString("D"));
        }
    }

    [Fact]
    public async Task When_ActivityPublicationFails_Then_TheDurableOccurrenceSurvivesForRecoveryWithoutItsSource()
    {
        // Given
        EntryShare share;
        EntryShareActivity activity;
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var oldestPending = await database.EntryShareActivities.Where(x => x.PublishedAt == null)
                .OrderBy(x => x.OccurredAt).Select(x => (Instant?)x.OccurredAt).FirstOrDefaultAsync();
            share = CreateShare((oldestPending ?? apiFactory.FakeClock.GetCurrentInstant()) - Duration.FromSeconds(1), false);
            activity = share.Activities.Single();
            database.EntryShareActivities.Add(activity);
            await database.SaveChangesAsync();
        }
        var options = Options.Create(new DispatchEntryShareActivityJobOptions { BatchSize = 1, MaximumBatches = 1 });
        var failingPublisher = Substitute.For<IPublishEndpoint>();
        failingPublisher.Publish(Arg.Any<EntryShareActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Simulated unavailable broker")));

        // When
        await using (var failureScope = apiFactory.Services.CreateAsyncScope())
        {
            var writer = failureScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            await Should.ThrowAsync<IOException>(() => new DispatchEntryShareActivityJob(writer, options,
                failingPublisher, apiFactory.FakeClock).ExecuteAsync());
        }

        // Then
        await using (var verificationScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            (await database.Set<EntryShareActivity>().SingleAsync(x => x.ShareId == share.Id)).PublishedAt.ShouldBeNull();
        }
        var publisher = Substitute.For<IPublishEndpoint>();
        await using (var recoveryScope = apiFactory.Services.CreateAsyncScope())
        {
            await new DispatchEntryShareActivityJob(recoveryScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                options, publisher, apiFactory.FakeClock).ExecuteAsync();
        }
        await publisher.Received(1).Publish(Arg.Is<EntryShareActivityEvent>(x =>
            x.ShareId == share.Id && x.Sequence == activity.Sequence), Arg.Any<CancellationToken>());
        await using var finalScope = apiFactory.Services.CreateAsyncScope();
        var finalDatabase = finalScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await finalDatabase.Set<EntryShareActivity>().SingleAsync(x => x.ShareId == share.Id)).PublishedAt.ShouldNotBeNull();
    }

    private async Task ForwardAsync(EntryShareActivityEvent message)
    {
        AppendAuditLogCommand? audit = null;
        RecordEntryShareReceiptCommand? receipt = null;
        var publisher = Substitute.For<IPublishEndpoint>();
        publisher.When(x => x.Publish(Arg.Any<AppendAuditLogCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => audit = call.Arg<AppendAuditLogCommand>());
        publisher.When(x => x.Publish(Arg.Any<RecordEntryShareReceiptCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => receipt = call.Arg<RecordEntryShareReceiptCommand>());
        await new OnEntryShareActivityAudit(publisher).Consume(apiFactory.MockConsumeContext(message));
        await new OnEntryShareActivityReceipt(publisher).Consume(apiFactory.MockConsumeContext(message));
        audit.ShouldNotBeNull();
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(audit);
        if (receipt is not null)
        {
            await apiFactory.ConsumeAsync<RecordEntryShareReceiptConsumer, RecordEntryShareReceiptCommand>(receipt);
        }
    }

    private static EntryShare CreateShare(Instant now, bool notify, EntryScope? source = null, Guid? senderId = null) => EntryShare.Create(Guid.NewGuid(),
        source ?? new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new EntryRevision(1), senderId ?? Guid.NewGuid(),
        now, now + Duration.FromHours(1), 2, EntryShareRecipientMode.AnyoneWithLink, null,
        EntryShareProtection.None, null, new byte[32], new byte[24], new byte[16], notify, 1, now);
}
