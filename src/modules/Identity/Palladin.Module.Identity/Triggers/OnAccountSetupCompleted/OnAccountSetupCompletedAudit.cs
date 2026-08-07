using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnAccountSetupCompletedAuditDefinition : ConsumerDefinition<OnAccountSetupCompletedAudit>
{
    public OnAccountSetupCompletedAuditDefinition() => EndpointName = IdentityEndpoints.Audit;
}

[UsedImplicitly]
internal sealed class OnAccountSetupCompletedAudit(IPublishEndpoint publishEndpoint) : IConsumer<AccountSetupCompletedEvent>
{
    public Task Consume(ConsumeContext<AccountSetupCompletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: AuditEventType.AccountSetupCompleted,
            ActorType: AuditActorType.User, Result: AuditResult.Succeeded,
            OccurredAt: msg.UpdatedAt,
            UserId: msg.UserId,
            AgentId: null,
            VaultId: null,
            EntryId: null,
                        AgentName: null,
            ActorName: msg.DisplayName,
                        IpAddress: null,
            Metadata: new Dictionary<string, string>()), context.CancellationToken);
    }
}
