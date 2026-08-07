using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnPushTokenRegisteredOnboardingDefinition : ConsumerDefinition<OnPushTokenRegisteredOnboarding>
{
    public OnPushTokenRegisteredOnboardingDefinition() => EndpointName = NotificationEndpoints.Onboarding;
}

// Registering a mobile (iOS / Android) push token is the "mobile registered" onboarding milestone.
// Web push tokens do not count. Notification owns push tokens, so it pushes the milestone to Identity.
[UsedImplicitly]
internal sealed class OnPushTokenRegisteredOnboarding(IPublishEndpoint publishEndpoint)
    : IConsumer<PushTokenRegisteredEvent>
{
    public Task Consume(ConsumeContext<PushTokenRegisteredEvent> context)
    {
        var msg = context.Message;
        if (msg.Platform is not (PushPlatform.Ios or PushPlatform.Android))
        {
            return Task.CompletedTask;
        }

        return publishEndpoint.Publish(
            new MarkOnboardingStepCommand(msg.UserId, OnboardingStep.MobileRegistered), context.CancellationToken);
    }
}
