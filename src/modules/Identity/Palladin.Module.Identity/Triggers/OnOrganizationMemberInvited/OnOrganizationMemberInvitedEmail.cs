using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedEmailDefinition
    : ConsumerDefinition<OnOrganizationMemberInvitedEmail>
{
    public OnOrganizationMemberInvitedEmailDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberInvitedEmail(IOptions<OrganizationInvitationOptions> options)
    : IConsumer<OrganizationMemberInvitedEvent>
{
    public Task Consume(ConsumeContext<OrganizationMemberInvitedEvent> context)
    {
        var msg = context.Message;
        var invitationUrl = $"{options.Value.AcceptanceUrlBase}?token={msg.Token}";

        return context.Publish(new SendEmailCommand(
            msg.Email,
            EmailTemplates.OrganizationInvitation,
            msg.Language,
            new Dictionary<string, string>
            {
                ["organizationName"] = msg.OrganizationName,
                ["invitedByName"] = msg.InvitedByName,
                ["role"] = msg.RoleName,
                ["invitationUrl"] = invitationUrl,
                ["expiryHours"] = msg.ExpiryHours.ToString(),
            },
            msg.OccurredAt,
            $"organization-invitation:{msg.InvitationId}"));
    }
}
