namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

// Output of template rendering — feeds straight into EmailMessage for IEmailSender.
internal sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody);
