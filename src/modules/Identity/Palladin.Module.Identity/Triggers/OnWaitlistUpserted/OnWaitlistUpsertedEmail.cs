using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistUpsertedEmailDefinition : ConsumerDefinition<OnWaitlistUpsertedEmail>
{
    public OnWaitlistUpsertedEmailDefinition() => EndpointName = IdentityEndpoints.Self;
}

// Event → command conversion: Identity owns the signup, Notification owns email delivery.
[UsedImplicitly]
internal sealed class OnWaitlistUpsertedEmail(IOptions<WaitlistOptions> options) : IConsumer<WaitlistUpsertedEvent>
{
    public Task Consume(ConsumeContext<WaitlistUpsertedEvent> context)
    {
        var msg = context.Message;
        var verificationUrl = $"{options.Value.VerificationUrlBase}?token={msg.Token}";

        return context.Publish(new SendEmailCommand(
            msg.Email,
            EmailTemplates.WaitlistVerification,
            msg.Language,
            new Dictionary<string, string>
            {
                ["verificationUrl"] = verificationUrl,
                ["expiryHours"] = msg.ExpiryHours.ToString(),
                ["baseUrl"] = new Uri(options.Value.VerifiedRedirectUrl)
                    .GetLeftPart(UriPartial.Authority),
            },
            msg.OccurredAt));
    }
}
