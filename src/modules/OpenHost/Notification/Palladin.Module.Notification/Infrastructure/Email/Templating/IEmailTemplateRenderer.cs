namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

// Renders a localized email (subject + HTML + text) from repo-versioned templates. The template
// engine stays behind this interface — it never leaks outside Infrastructure/Email.
internal interface IEmailTemplateRenderer
{
    // language is an ISO 639-1 code (e.g. "en", "pl"); unsupported values fall back to English.
    RenderedEmail Render(string templateName, string language, IReadOnlyDictionary<string, object?> model);
}
