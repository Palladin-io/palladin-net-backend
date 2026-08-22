using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeactivationCompletedDefinition
    : ConsumerDefinition<OnAgentDeactivationCompleted>
{
    public OnAgentDeactivationCompletedDefinition() => EndpointName = AgentsEndpoints.FromVault;
}

[UsedImplicitly]
internal sealed class OnAgentDeactivationCompleted(
    AgentsDomainWriteContext domainWriteContext)
    : IConsumer<AgentDeactivationCompletedEvent>
{
    public async Task Consume(ConsumeContext<AgentDeactivationCompletedEvent> context)
    {
        var message = context.Message;
        var agent = await domainWriteContext.Agents
            .Include(x => x.DeactivatedByUser)
            .SingleOrDefaultAsync(x => x.OrganizationId == message.OrganizationId && x.Id == message.AgentId,
                context.CancellationToken);
        if (agent is null || agent.Status == Palladin.Core.Types.AgentStatus.Deactivated)
        {
            return;
        }

        var matchesActiveRequest = agent.Status == Palladin.Core.Types.AgentStatus.Deactivating
                                   && agent.DeactivationRequestId == message.RequestId;
        if (!matchesActiveRequest && message.UpdatedAt <= agent.UpdatedAt)
        {
            return;
        }

        if (!matchesActiveRequest || agent.DeactivatedByUser is null)
        {
            throw new InvalidOperationException("Agent deactivation completion does not match the active request.");
        }

        agent.CompleteDeactivation(agent.DeactivatedByUser.DisplayName, message.CompletedAt);
        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
