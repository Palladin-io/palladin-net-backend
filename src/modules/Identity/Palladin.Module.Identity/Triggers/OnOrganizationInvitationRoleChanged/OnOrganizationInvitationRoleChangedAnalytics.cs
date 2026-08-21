using JetBrains.Annotations;
using MassTransit;
using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationRoleChangedAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationInvitationRoleChangedAnalytics>
{
    public OnOrganizationInvitationRoleChangedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationRoleChangedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationInvitationRoleChangedEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationRoleChangedEvent> context)
    {
        var message = context.Message;
        analyticsService.CaptureEvent(
            message.ChangedBy.ToString(),
            "identity",
            "org-invitation-role-changed");
        return Task.CompletedTask;
    }
}
