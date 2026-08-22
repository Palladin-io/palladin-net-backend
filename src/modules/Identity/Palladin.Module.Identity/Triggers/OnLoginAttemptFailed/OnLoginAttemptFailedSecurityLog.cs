using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Logging;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnLoginAttemptFailedSecurityLogDefinition
    : ConsumerDefinition<OnLoginAttemptFailedSecurityLog>
{
    public OnLoginAttemptFailedSecurityLogDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnLoginAttemptFailedSecurityLog(
    ILogger<OnLoginAttemptFailedSecurityLog> logger)
    : IConsumer<LoginAttemptFailedEvent>
{
    public Task Consume(ConsumeContext<LoginAttemptFailedEvent> context)
    {
        var msg = context.Message;
        logger.LogWarning(
            "Authentication attempt {AttemptId} failed for account hash {EmailHash} from IP {IpAddress} using {Factor}",
            msg.AttemptId,
            msg.EmailHash,
            msg.IpAddress,
            msg.Factor);
        return Task.CompletedTask;
    }
}
