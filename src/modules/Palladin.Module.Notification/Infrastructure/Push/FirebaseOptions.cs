namespace Palladin.Module.Notification.Infrastructure.Push;

internal sealed class FirebaseOptions
{
    public const string Position = "Modules:Notification:Firebase";

    // Absolute path to the Firebase service-account JSON. When null/empty the SDK falls back to
    // GOOGLE_APPLICATION_CREDENTIALS. When neither is available, push sending degrades to a no-op.
    public string? ServiceAccountJsonPath { get; init; }

    // Click-through base URL embedded in web push data for the Service Worker.
    public string WebClickBaseUrl { get; init; } = "https://palladin.io";
}
