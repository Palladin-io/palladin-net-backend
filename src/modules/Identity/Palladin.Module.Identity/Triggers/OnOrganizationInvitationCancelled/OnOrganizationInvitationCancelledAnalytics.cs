using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationCancelledAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationInvitationCancelledAnalytics>
{
    public OnOrganizationInvitationCancelledAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationCancelledAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationInvitationCancelledEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationCancelledEvent> context)
    {
        var message = context.Message;
        analyticsService.CaptureEvent(
            message.CancelledBy.ToString(),
            "identity",
            "org-invitation-cancelled");
        return Task.CompletedTask;
    }
}
