using System.Globalization;
using JetBrains.Annotations;
using MassTransit;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnLoginLockedOutEmailDefinition : ConsumerDefinition<OnLoginLockedOutEmail>
{
    public OnLoginLockedOutEmailDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnLoginLockedOutEmail : IConsumer<LoginLockedOutEvent>
{
    public Task Consume(ConsumeContext<LoginLockedOutEvent> context)
    {
        var msg = context.Message;
        if (msg.TargetUserId is null || string.IsNullOrWhiteSpace(msg.RecipientEmail))
        {
            return Task.CompletedTask;
        }

        var alertBucket = msg.OccurredAt.ToUnixTimeSeconds() / (msg.LockoutMinutes * 60L);
        return context.Publish(new SendEmailCommand(
            msg.RecipientEmail,
            EmailTemplates.LoginLockoutAlert,
            msg.PreferredLanguage ?? "en",
            new Dictionary<string, string>
            {
                ["attemptCount"] = msg.AttemptCount.ToString(CultureInfo.InvariantCulture),
                ["windowMinutes"] = msg.WindowMinutes.ToString(CultureInfo.InvariantCulture),
                ["ipAddresses"] = string.Join(", ", msg.SourceIpAddresses),
                ["occurredAt"] = FormatUtc(msg.OccurredAt),
                ["lockedUntil"] = FormatUtc(msg.LockedUntil),
            },
            msg.OccurredAt,
            $"identity:login-lockout:{msg.TargetUserId:N}:{alertBucket}"));
    }

    private static string FormatUtc(NodaTime.Instant instant) =>
        instant.InUtc().ToDateTimeUtc().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
