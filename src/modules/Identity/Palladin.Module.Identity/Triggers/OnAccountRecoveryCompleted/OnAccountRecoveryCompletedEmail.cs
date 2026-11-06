using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnAccountRecoveryCompletedEmailDefinition : ConsumerDefinition<OnAccountRecoveryCompletedEmail>
{
    public OnAccountRecoveryCompletedEmailDefinition() => EndpointName = IdentityEndpoints.Self;
}

// Tells the owner that the recovery phrase was used: time and approximate client only, never key material.
[UsedImplicitly]
internal sealed class OnAccountRecoveryCompletedEmail : IConsumer<AccountRecoveryCompletedEvent>
{
    public Task Consume(ConsumeContext<AccountRecoveryCompletedEvent> context)
    {
        var msg = context.Message;
        return context.Publish(new SendEmailCommand(
            msg.Email,
            EmailTemplates.AccountRecovered,
            msg.PreferredLanguage,
            new Dictionary<string, string>
            {
                ["occurredAt"] = msg.UpdatedAt.InUtc().ToDateTimeUtc()
                    .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                ["client"] = msg.Client,
            },
            msg.UpdatedAt,
            $"identity:account-recovered:{msg.UserId:N}:{msg.UpdatedAt.ToUnixTimeMilliseconds()}"));
    }
}
