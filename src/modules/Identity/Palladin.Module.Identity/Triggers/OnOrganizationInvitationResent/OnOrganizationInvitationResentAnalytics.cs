using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationInvitationResentAnalytics>
{
    public OnOrganizationInvitationResentAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationInvitationResentEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationResentEvent> context)
    {
        var message = context.Message;
        analyticsService.CaptureEvent(
            message.ResentBy.ToString(),
            "identity",
            "org-invitation-resent");
        return Task.CompletedTask;
    }
}
