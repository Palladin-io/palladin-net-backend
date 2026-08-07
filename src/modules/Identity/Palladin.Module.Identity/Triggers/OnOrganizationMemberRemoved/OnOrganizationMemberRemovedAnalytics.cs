using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovedAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationMemberRemovedAnalytics>
{
    public OnOrganizationMemberRemovedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationMemberRemovedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRemovedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.RemovedBy.ToString(), "identity", "org-member-removed");
        return Task.CompletedTask;
    }
}
