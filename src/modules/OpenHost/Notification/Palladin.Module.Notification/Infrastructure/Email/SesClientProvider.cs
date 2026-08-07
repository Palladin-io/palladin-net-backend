using Amazon.SimpleEmailV2;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Notification.Infrastructure.Email;

// Builds the SES client once. This is a concrete implementation detail behind IEmailSender — there is
// no provider interface. When no verified sender is configured the client stays null and the email
// channel degrades to a logged no-op (the app still boots and every non-email behaviour works).
[UsedImplicitly]
internal sealed class SesClientProvider
{
    public IAmazonSimpleEmailServiceV2? Client { get; }

    public SesClientProvider(IOptions<SesOptions> options, ILogger<SesClientProvider> logger)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.FromAddress))
        {
            logger.LogWarning("SES sender not configured (empty FromAddress) — email channel disabled (no-op).");
            return;
        }

        Client = AwsClientFactory.Build(
            opts,
            () => new AmazonSimpleEmailServiceV2Config(),
            (credentials, config) => new AmazonSimpleEmailServiceV2Client(credentials, config),
            config => new AmazonSimpleEmailServiceV2Client(config));
    }
}
