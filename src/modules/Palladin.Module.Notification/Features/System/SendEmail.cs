using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Features;

[UsedImplicitly]
internal sealed class SendEmailConsumerDefinition : ConsumerDefinition<SendEmailConsumer>
{
    public SendEmailConsumerDefinition() => EndpointName = NotificationEndpoints.Email;
}

// The single entry point of the email channel for other modules: render the requested template in
// the requested language and hand the result to IEmailSender. Suppression and provider concerns
// stay behind the sender.
[UsedImplicitly]
internal sealed class SendEmailConsumer(
    IEmailTemplateRenderer templateRenderer,
    IEmailDispatchDeduplicator dispatchDeduplicator) : IConsumer<SendEmailCommand>
{
    public async Task Consume(ConsumeContext<SendEmailCommand> context)
    {
        var msg = context.Message;

        var model = msg.Model.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);
        var rendered = templateRenderer.Render(msg.Template, msg.Language, model);

        await dispatchDeduplicator.SendOnceAsync(
            msg.IdempotencyKey,
            new EmailMessage(msg.Email, rendered.Subject, rendered.HtmlBody, rendered.TextBody),
            context.CancellationToken);
    }
}
