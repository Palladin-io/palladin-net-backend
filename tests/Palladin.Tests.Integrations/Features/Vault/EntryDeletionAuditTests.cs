using MassTransit;
using NodaTime;
using NSubstitute;
using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared.Fakers;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

public sealed class EntryDeletionAuditTests
{
    [Theory]
    [InlineData(EntryState.Active)]
    [InlineData(EntryState.Archived)]
    public async Task When_EntryIsDeleted_Then_AuditAndAnalyticsReportDeletion(EntryState initialState)
    {
        // Given
        var entry = EntryFaker.Create().RuleFor(x => x.State, initialState).Generate();
        var actor = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 10, 7, 12, 0);
        var request = EntryEnvelopeFaker.CreateStateChangeRequest(
            entry.OrganizationId, entry.VaultId, entry.Id, 1, EntryOperation.Deleted);
        var publisher = Substitute.For<IPublishEndpoint>();
        var analytics = Substitute.For<IAnalyticsService>();

        // When
        entry.Delete(new EntryRevision(1), new MemberKeyGeneration(1), new VaultKeyVersion(1),
            null, VaultEnvelopeContractMapper.ToDomain(request.MemberSecret),
            VaultEnvelopeContractMapper.ToDomain(request.MemberIndex),
            new AllocatedVaultSequences(new MemberSequence(2), null), now, actor);
        var message = entry.FetchEvents().OfType<EntryUpsertedEvent>().Single();
        var context = Substitute.For<ConsumeContext<EntryUpsertedEvent>>();
        context.Message.Returns(message);
        await new OnEntryUpsertedAudit(publisher).Consume(context);
        await new OnEntryUpserted(analytics).Consume(context);

        // Then
        entry.State.ShouldBe(EntryState.Deleted);
        await publisher.Received(1).Publish(Arg.Is<AppendAuditLogCommand>(command =>
            command.EventType == AuditEventType.EntryDeleted
            && command.OrganizationId == entry.OrganizationId
            && command.VaultId == entry.VaultId && command.EntryId == entry.Id
            && command.UserId == actor && command.OccurredAt == now
            && command.Metadata!["revision"] == "2"), Arg.Any<CancellationToken>());
        await publisher.DidNotReceive().Publish(Arg.Is<AppendAuditLogCommand>(command =>
            command.EventType == AuditEventType.EntryUpdated), Arg.Any<CancellationToken>());
        analytics.Received(1).CaptureEvent(actor.ToString(), "vault", "entry-deleted",
            Arg.Any<Dictionary<string, object>>());
        analytics.DidNotReceive().CaptureEvent(Arg.Any<string>(), "vault", "entry-updated",
            Arg.Any<Dictionary<string, object>>());
    }
}
