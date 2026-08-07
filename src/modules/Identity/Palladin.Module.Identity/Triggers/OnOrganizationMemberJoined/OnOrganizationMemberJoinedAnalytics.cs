using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationMemberJoinedAnalytics>
{
    public OnOrganizationMemberJoinedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberJoinedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationMemberJoinedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberJoinedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.UserId.ToString(), "identity", "org-member-joined");
        return Task.CompletedTask;
    }
}
