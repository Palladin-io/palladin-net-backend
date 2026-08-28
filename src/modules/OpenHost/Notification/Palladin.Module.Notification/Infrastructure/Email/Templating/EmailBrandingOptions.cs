using JetBrains.Annotations;

namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

[UsedImplicitly]
internal sealed class EmailBrandingOptions
{
    // Leaf section name only — the module prefix (Modules:Notification) is composed at registration.
    public const string Position = "EmailBranding";

    // Values are trusted (config, not user input) and are exposed to every email template as the
    // shared branding chrome (header/footer).
    public string AppName { get; init; } = "Palladin";
    public string BaseUrl { get; init; } = "https://palladin.io";
    public string SupportEmail { get; init; } = "support@palladin.io";
    public string MobileAppUrl { get; init; } = "https://palladin.io/#features";
    public string BrowserExtensionUrl { get; init; } = "https://palladin.io/#features";
    public string? LogoUrl { get; init; }
}
