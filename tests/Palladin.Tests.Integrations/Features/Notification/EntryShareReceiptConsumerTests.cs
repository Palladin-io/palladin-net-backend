using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class EntryShareReceiptConsumerTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ConcurrentReceiptCommandsAreRetried_Then_TheSenderHasExactlyOneInboxItem()
    {
        // Given
        var command = new RecordEntryShareReceiptCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());

        // When
        var errors = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Record.ExceptionAsync(() =>
            apiFactory.ConsumeAsync<RecordEntryShareReceiptConsumer, RecordEntryShareReceiptCommand>(command)).AsTask()));
        foreach (var error in errors.Where(x => x is not null))
        {
            var databaseError = error.ShouldBeOfType<DbUpdateException>();
            var postgres = databaseError.InnerException.ShouldBeOfType<PostgresException>();
            postgres.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            postgres.ConstraintName.ShouldBeOneOf("PK_InboxItems", "IX_InboxItems_OrganizationId_UserId_Type_SubjectId");
        }
        await apiFactory.ConsumeAsync<RecordEntryShareReceiptConsumer, RecordEntryShareReceiptCommand>(command);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var row = await database.InboxItems.SingleAsync(x => x.OrganizationId == command.OrganizationId);
        row.Id.ShouldBe(command.ShareId);
        row.UserId.ShouldBe(command.SenderId);
        row.Type.ShouldBe(NotificationType.EntryShareReceived);
    }

    [Fact]
    public async Task When_AReceiptIsSubmittedThroughGenericFanout_Then_ItCannotBroadcastToTheOrganization()
    {
        // Given
        var command = new BroadcastNotificationCommand(Guid.NewGuid(), NotificationType.EntryShareReceived,
            NotificationCategory.Update, "notification.entry_share_received.title", new Dictionary<string, string>(),
            [], null, Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());

        // When
        await Should.ThrowAsync<InvalidOperationException>(() =>
            apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await database.InboxItems.AnyAsync(x => x.OrganizationId == command.OrganizationId)).ShouldBeFalse();
    }
}
