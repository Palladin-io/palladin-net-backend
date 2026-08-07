using Palladin.Core.Analytics;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRoleChangedAnalyticsDefinition
    : ConsumerDefinition<OnOrganizationMemberRoleChangedAnalytics>
{
    public OnOrganizationMemberRoleChangedAnalyticsDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRoleChangedAnalytics(IAnalyticsService analyticsService)
    : IConsumer<OrganizationMemberRoleChangedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberRoleChangedEvent> context)
    {
        var msg = context.Message;
        analyticsService.CaptureEvent(msg.ChangedBy.ToString(), "identity", "org-member-role-changed",
            new Dictionary<string, object>
            {
                ["old_role_count"] = msg.OldRoles.Count,
                ["new_role_count"] = msg.NewRoles.Count,
            });
        return Task.CompletedTask;
    }
}
