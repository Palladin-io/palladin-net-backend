using JetBrains.Annotations;
using MassTransit;
using NodaTime.Text;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnWaitlistDeveloperBenefitActivatedEmailDefinition
    : ConsumerDefinition<OnWaitlistDeveloperBenefitActivatedEmail>
{
    public OnWaitlistDeveloperBenefitActivatedEmailDefinition() =>
        EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnWaitlistDeveloperBenefitActivatedEmail
    : IConsumer<WaitlistDeveloperBenefitActivatedEvent>
{
    public Task Consume(ConsumeContext<WaitlistDeveloperBenefitActivatedEvent> context)
    {
        var message = context.Message;
        return context.Publish(new SendEmailCommand(
            message.Email,
            EmailTemplates.WaitlistDeveloperBenefitActivated,
            message.Language,
            new Dictionary<string, string>
            {
                ["startsAtUtc"] = InstantPattern.ExtendedIso.Format(message.StartsAt),
                ["endsAtUtc"] = InstantPattern.ExtendedIso.Format(message.EndsAt),
            },
            message.UpdatedAt,
            $"waitlist-developer-benefit:{message.UserId}"));
    }
}
