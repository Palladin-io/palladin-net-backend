using Palladin.Core.Guid;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.MassTransit;
using Palladin.Module.Audit.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Audit.Features;

[UsedImplicitly]
internal sealed class AppendAuditLogConsumerDefinition : ConsumerDefinition<AppendAuditLogConsumer>
{
    public AppendAuditLogConsumerDefinition() => EndpointName = AuditEndpoints.General;
}

[UsedImplicitly]
internal sealed class AppendAuditLogConsumer(
    AuditDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : IConsumer<AppendAuditLogCommand>
{
    public async Task Consume(ConsumeContext<AppendAuditLogCommand> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var alreadyLogged = msg.IdempotencyKey is { } idempotencyKey
            ? await domainWriteContext.AuditLogEntries.AnyAsync(e => e.Id == idempotencyKey, ct)
            : await domainWriteContext.AuditLogEntries.AnyAsync(
                e => !e.HasExplicitOccurrenceId && e.OrganizationId == msg.OrganizationId
                     && e.EventType == msg.EventType
                     && e.VaultId == msg.VaultId
                     && e.AgentId == msg.AgentId
                     && e.EntryId == msg.EntryId
                     && e.OccurredAt == msg.OccurredAt,
                ct);
        if (alreadyLogged)
        {
            return;
        }

        domainWriteContext.Add(AuditLogEntry.Create(
            msg.IdempotencyKey ?? guidProvider.Generate(),
            msg.OrganizationId,
            msg.EventType,
            msg.ActorType,
            msg.Result,
            msg.OccurredAt,
            clock.GetCurrentInstant(),
            msg.UserId,
            msg.AgentId,
            msg.VaultId,
            msg.EntryId,
            msg.AgentName,
            msg.ActorName,
            msg.IpAddress,
            msg.Metadata,
            hasExplicitOccurrenceId: msg.IdempotencyKey.HasValue));

        await domainWriteContext.CommitAsync(ct);
    }
}
