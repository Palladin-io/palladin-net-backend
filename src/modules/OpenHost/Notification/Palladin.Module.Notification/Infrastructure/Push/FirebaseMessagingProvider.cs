using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Notification.Infrastructure.Push;

// Singleton: initializes the Firebase app once. If no service-account credentials are available
// (local dev / CI), Messaging stays null and the push pipeline degrades to a logged no-op — the
// application still starts and all non-push behaviour works.
[UsedImplicitly]
internal sealed class FirebaseMessagingProvider : IFirebaseMessagingProvider
{
    private const string AppName = "palladin-notifications";

    public FirebaseMessaging? Messaging { get; }

    public FirebaseMessagingProvider(
        IOptions<FirebaseOptions> options,
        ILogger<FirebaseMessagingProvider> logger)
    {
        var credential = ResolveCredential(options.Value, logger);
        if (credential is null)
        {
            logger.LogWarning(
                "Firebase credentials not configured — push notifications are disabled (no-op).");
            return;
        }

        var app = FirebaseApp.GetInstance(AppName)
                  ?? FirebaseApp.Create(new AppOptions { Credential = credential }, AppName);
        Messaging = FirebaseMessaging.GetMessaging(app);
    }

    private static GoogleCredential? ResolveCredential(FirebaseOptions options, ILogger logger)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(options.ServiceAccountJsonPath) && File.Exists(options.ServiceAccountJsonPath))
            {
                return GoogleCredential.FromFile(options.ServiceAccountJsonPath);
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            {
                return GoogleCredential.GetApplicationDefault();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load Firebase credentials — push notifications disabled.");
        }

        return null;
    }
}
