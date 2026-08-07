using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalRequestedDefinition
    : ConsumerDefinition<OnOrganizationMemberRemovalRequested>
{
    public OnOrganizationMemberRemovalRequestedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalRequested(
    VaultPrincipalDeprovisioningCoordinator coordinator)
    : IConsumer<OrganizationMemberRemovalRequestedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRemovalRequestedEvent> context)
    {
        var message = context.Message;
        return coordinator.StartAsync(
            message.RequestId,
            message.OrganizationId,
            VaultPrincipalType.OrganizationMember,
            message.UserId,
            message.RequestedBy,
            message.RequestedAt,
            context.CancellationToken);
    }
}
