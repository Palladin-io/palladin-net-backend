using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using NodaTime;
using NodaTime.Text;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Palladin.Module.Notification.Infrastructure.MassTransit;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class SendEntryShareVerificationEmailConsumerDefinition : ConsumerDefinition<SendEntryShareVerificationEmailConsumer>
{
    public SendEntryShareVerificationEmailConsumerDefinition() => EndpointName = NotificationEndpoints.Email;
}

[UsedImplicitly]
internal sealed class SendEntryShareVerificationEmailConsumer(
    IEmailTemplateRenderer renderer, IEmailDispatchDeduplicator deduplicator, IClock clock)
    : IConsumer<SendEntryShareVerificationEmailCommand>
{
    public async Task Consume(ConsumeContext<SendEntryShareVerificationEmailCommand> context)
    {
        var message = context.Message;
        if (message.ExpiresAt <= clock.GetCurrentInstant())
        {
            return;
        }

        if (message.ShareId == Guid.Empty || message.SessionId == Guid.Empty || message.Generation < 1
            || message.Code is not { Length: 6 } || !message.Code.All(char.IsAsciiDigit)
            || string.IsNullOrWhiteSpace(message.Email) || message.Email.Length > 320
            || message.Language is not ("en" or "pl") || message.ExpiresAt <= message.OccurredAt)
        {
            throw new DomainException("Invalid sharing verification email contract.");
        }

        var generation = message.Generation.ToString(CultureInfo.InvariantCulture);
        var rendered = renderer.Render(EmailTemplates.EntryShareVerification, message.Language,
            new Dictionary<string, object?>
            {
                ["code"] = message.Code,
                ["generation"] = generation,
                ["expiresAtUtc"] = InstantPattern.General.Format(message.ExpiresAt),
            });
        await deduplicator.SendOnceAsync($"entry-share-otp:{message.ShareId:D}:{message.SessionId:D}:{generation}",
            new EmailMessage(message.Email, rendered.Subject, rendered.HtmlBody, rendered.TextBody), context.CancellationToken);
    }
}
