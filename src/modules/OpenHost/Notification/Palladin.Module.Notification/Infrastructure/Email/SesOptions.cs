using JetBrains.Annotations;

namespace Palladin.Module.Notification.Infrastructure.Email;

[UsedImplicitly]
internal sealed class SesOptions
{
    // Leaf section name only — the module prefix (Modules:Notification) is composed at registration.
    public const string Position = "Ses";

    public string Region { get; init; } = "eu-west-1";

    // Explicit credentials for the send-only IAM user. When null/empty the SDK falls back to the
    // ambient credential chain (IAM role via instance metadata). When neither is available the
    // email channel degrades to a logged no-op — the app still boots.
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }

    // SES configuration set that routes bounce/complaint/delivery events to SNS. Optional.
    public string? ConfigurationSetName { get; init; }

    // Verified sender identity. Empty disables the channel (no-op).
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "Palladin";

    // LocalStack SES endpoint override for local dev and tests. Empty for native AWS.
    public string? ServiceUrl { get; init; }

    // SQS queue fed by the SES bounce/complaint SNS topic. Empty disables the event poller (no-op).
    public string? EventsQueueUrl { get; init; }
}
