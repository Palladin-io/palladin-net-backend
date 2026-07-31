using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palladin.Module.Notification.Infrastructure.Email.Suppression;

namespace Palladin.Module.Notification.Infrastructure.Email;

[UsedImplicitly]
internal sealed class SesEmailSender(
    IAmazonSimpleEmailServiceV2? client,
    IEmailSuppressionStore suppressionStore,
    IOptions<SesOptions> options,
    ILogger<SesEmailSender> logger) : IEmailSender
{
    private const string Charset = "UTF-8";

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (client is null)
        {
            logger.LogWarning("SES not configured — dropped email to {Recipient}.", Mask(message.ToAddress));
            return;
        }

        if (await suppressionStore.IsSuppressedAsync(message.ToAddress, ct))
        {
            logger.LogInformation("Recipient {Recipient} is suppressed — skipping send.", Mask(message.ToAddress));
            return;
        }

        var request = BuildRequest(message, options.Value);
        var response = await client.SendEmailAsync(request, ct);

        logger.LogInformation(
            "Sent email to {Recipient} (messageId {MessageId}).", Mask(message.ToAddress), response.MessageId);
    }

    private static SendEmailRequest BuildRequest(EmailMessage message, SesOptions opts)
    {
        var body = new Body { Html = new Content { Data = message.HtmlBody, Charset = Charset } };
        if (message.TextBody is not null)
        {
            body.Text = new Content { Data = message.TextBody, Charset = Charset };
        }

        var request = new SendEmailRequest
        {
            FromEmailAddress = FormatSender(opts),
            Destination = new Destination { ToAddresses = [message.ToAddress] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = message.Subject, Charset = Charset },
                    Body = body,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(opts.ConfigurationSetName))
        {
            request.ConfigurationSetName = opts.ConfigurationSetName;
        }

        return request;
    }

    private static string FormatSender(SesOptions opts) =>
        string.IsNullOrWhiteSpace(opts.FromName) ? opts.FromAddress : $"{opts.FromName} <{opts.FromAddress}>";

    // Recipient addresses are PII — never log them in full.
    private static string Mask(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        return $"{address[0]}***{address[at..]}";
    }
}
