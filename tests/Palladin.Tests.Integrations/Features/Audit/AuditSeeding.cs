using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using NodaTime;

namespace Palladin.Tests.Integrations.Features.Audit;

// Seeds opaque audit rows the OpenHost way: build the AppendAuditLogCommand an owning
// module would publish, then run Audit's real AppendAuditLogConsumer. Each seeded row gets a distinct
// OccurredAt so rows that differ only by a non-key field (e.g. UserId) are not collapsed by the
// consumer's natural-key idempotency.
internal static class AuditSeeding
{
    private static long _sequence;

    private static Instant NextOccurredAt(ApiFactory apiFactory) =>
        apiFactory.FakeClock.GetCurrentInstant().Plus(Duration.FromMilliseconds(Interlocked.Increment(ref _sequence)));

    public static Task AppendAsync(ApiFactory apiFactory, AppendAuditLogCommand command) =>
        apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(command);

    public static Task SeedRowAsync(
        ApiFactory apiFactory,
        Guid orgId,
        string eventType,
        Guid? agentId = null,
        Guid? userId = null) =>
        AppendAsync(apiFactory, new AppendAuditLogCommand(
            OrganizationId: orgId,
            EventType: eventType,
            ActorType: agentId is not null ? AuditActorType.Agent : AuditActorType.User,
            Result: AuditResult.Succeeded,
            OccurredAt: NextOccurredAt(apiFactory),
            UserId: userId,
            AgentId: agentId,
            VaultId: null,
            EntryId: null,
            AgentName: null,
            ActorName: null,
            IpAddress: null,
            Metadata: new Dictionary<string, string>()));

    public static Task SeedGrantApprovedAsync(
        ApiFactory apiFactory,
        Guid orgId,
        Guid vaultId,
        Guid approver,
        Guid? entryId = null,
        string agentName = "agent",
        string actorName = "actor") =>
        AppendAsync(apiFactory, new AppendAuditLogCommand(
            OrganizationId: orgId,
            EventType: AuditEventType.GrantApproved,
            ActorType: AuditActorType.User,
            Result: AuditResult.Succeeded,
            OccurredAt: NextOccurredAt(apiFactory),
            UserId: approver,
            AgentId: null,
            VaultId: vaultId,
            EntryId: entryId ?? Guid.NewGuid(),
            AgentName: agentName,
            ActorName: actorName,
            IpAddress: null,
            Metadata: new Dictionary<string, string>()));
}
