using JetBrains.Annotations;

namespace Palladin.Module.Notification.Infrastructure.Email;

// Rendered, ready-to-send message. Subject and bodies are produced by the upstream template renderer;
// this channel does not compose content. TextBody is optional and omitted from the SES
// request when null so a plain-text part is only sent when a caller supplies one.
[PublicAPI]
internal sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string? TextBody = null);
