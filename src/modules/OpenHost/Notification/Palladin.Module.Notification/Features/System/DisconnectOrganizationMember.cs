using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.SignalR;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class DisconnectOrganizationMemberConsumerDefinition
    : ConsumerDefinition<DisconnectOrganizationMemberConsumer>
{
    public DisconnectOrganizationMemberConsumerDefinition() => EndpointName = NotificationEndpoints.Realtime;
}

[UsedImplicitly]
internal sealed class DisconnectOrganizationMemberConsumer(RealtimeConnectionRegistry registry)
    : IConsumer<DisconnectOrganizationMemberCommand>
{
    public Task Consume(ConsumeContext<DisconnectOrganizationMemberCommand> context)
    {
        registry.Abort(context.Message.OrganizationId, context.Message.UserId, context.Message.AuthorizationVersion);
        return Task.CompletedTask;
    }
}
