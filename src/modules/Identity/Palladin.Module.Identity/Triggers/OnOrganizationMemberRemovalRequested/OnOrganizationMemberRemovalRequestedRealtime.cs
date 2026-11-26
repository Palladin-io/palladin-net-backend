using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalRequestedRealtimeDefinition
    : ConsumerDefinition<OnOrganizationMemberRemovalRequestedRealtime>
{
    public OnOrganizationMemberRemovalRequestedRealtimeDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalRequestedRealtime
    : IConsumer<OrganizationMemberRemovalRequestedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRemovalRequestedEvent> context) =>
        context.Publish(new DisconnectOrganizationMemberCommand(
            context.Message.OrganizationId,
            context.Message.UserId,
            context.Message.AuthorizationVersion,
            context.Message.RequestedAt));
}
