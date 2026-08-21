using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Options;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Invitations;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentEmailDefinition
    : ConsumerDefinition<OnOrganizationInvitationResentEmail>
{
    public OnOrganizationInvitationResentEmailDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationInvitationResentEmail(IOptions<OrganizationInvitationOptions> options)
    : IConsumer<OrganizationInvitationResentEvent>
{
    public Task Consume(ConsumeContext<OrganizationInvitationResentEvent> context)
    {
        var message = context.Message;
        var invitationUrl = $"{options.Value.AcceptanceUrlBase}?token={message.Token}";

        return context.Publish(new SendEmailCommand(
            message.Email,
            EmailTemplates.OrganizationInvitation,
            message.Language,
            new Dictionary<string, string>
            {
                ["organizationName"] = message.OrganizationName,
                ["invitedByName"] = message.ResentByName,
                ["role"] = message.RoleName,
                ["invitationUrl"] = invitationUrl,
                ["expiryHours"] = message.ExpiryHours.ToString(),
            },
            message.OccurredAt,
            $"organization-invitation:{message.InvitationId}:resend:{message.ResendId}"));
    }
}
