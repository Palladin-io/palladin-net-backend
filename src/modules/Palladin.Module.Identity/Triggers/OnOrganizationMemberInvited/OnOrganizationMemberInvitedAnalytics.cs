using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationMemberInvitedAnalytics>
{
    public OnOrganizationMemberInvitedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationMemberInvitedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberInvitedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.InvitedBy.ToString(), "identity", "org-member-invited");
        return Task.CompletedTask;
    }
}
