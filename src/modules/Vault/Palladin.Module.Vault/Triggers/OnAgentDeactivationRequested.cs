using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeactivationRequestedDefinition
    : ConsumerDefinition<OnAgentDeactivationRequested>
{
    public OnAgentDeactivationRequestedDefinition() => EndpointName = VaultEndpoints.FromAgents;
}

[UsedImplicitly]
internal sealed class OnAgentDeactivationRequested(
    VaultPrincipalDeprovisioningCoordinator coordinator)
    : IConsumer<AgentDeactivationRequestedEvent>
{
    public Task Consume(ConsumeContext<AgentDeactivationRequestedEvent> context)
    {
        var message = context.Message;
        return coordinator.StartAsync(
            message.RequestId,
            message.OrganizationId,
            VaultPrincipalType.Agent,
            message.AgentId,
            message.RequestedBy,
            message.RequestedAt,
            context.CancellationToken);
    }
}
