using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnEmailVerificationRequestedDefinition : ConsumerDefinition<OnEmailVerificationRequested>
{
    public OnEmailVerificationRequestedDefinition() => EndpointName = IdentityEndpoints.Self;
}

// Event → command conversion: Identity owns the account, Notification owns email delivery.
[UsedImplicitly]
internal sealed class OnEmailVerificationRequested(IOptions<EmailVerificationOptions> options)
    : IConsumer<EmailVerificationRequestedEvent>
{
    public Task Consume(ConsumeContext<EmailVerificationRequestedEvent> context)
    {
        var msg = context.Message;
        var verificationUrl = $"{options.Value.VerificationUrlBase}?token={msg.Token}";

        return context.Publish(new SendEmailCommand(
            msg.Email,
            EmailTemplates.EmailVerification,
            msg.Language,
            new Dictionary<string, string>
            {
                ["verificationUrl"] = verificationUrl,
                ["expiryMinutes"] = msg.ExpiryMinutes.ToString(),
            },
            msg.OccurredAt));
    }
}
