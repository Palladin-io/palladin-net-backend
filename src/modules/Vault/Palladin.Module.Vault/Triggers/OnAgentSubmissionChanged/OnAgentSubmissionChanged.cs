using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentSubmissionChangedDefinition : ConsumerDefinition<OnAgentSubmissionChanged>
{
    public OnAgentSubmissionChangedDefinition() => EndpointName = VaultEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnAgentSubmissionChanged(IAnalyticsService analyticsService)
    : IConsumer<AgentSubmissionChangedEvent>
{
    public Task Consume(ConsumeContext<AgentSubmissionChangedEvent> context)
    {
        var msg = context.Message;
        var (distinctId, eventName) = msg.Action switch
        {
            AgentSubmissionAction.Submitted => (msg.AgentId, "agent-submission-created"),
            AgentSubmissionAction.Approved => (msg.DecidedBy ?? msg.AgentId, "agent-submission-approved"),
            AgentSubmissionAction.Denied => (msg.DecidedBy ?? msg.AgentId, "agent-submission-denied"),
            _ => (Guid.Empty, string.Empty),
        };
        if (distinctId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        analyticsService.CaptureEvent(distinctId.ToString(), "vault", eventName, new Dictionary<string, object>
        {
            ["submission_kind"] = msg.SubmissionKind.ToString(),
            ["entry_type"] = msg.EntryType.ToString(),
            ["with_grant"] = msg.DecisionGrantId is not null,
        });
        return Task.CompletedTask;
    }
}
