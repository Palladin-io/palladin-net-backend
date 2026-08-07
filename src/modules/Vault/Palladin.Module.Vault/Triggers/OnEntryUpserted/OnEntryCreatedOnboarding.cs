using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnEntryCreatedOnboardingDefinition : ConsumerDefinition<OnEntryCreatedOnboarding>
{
    public OnEntryCreatedOnboardingDefinition() => EndpointName = VaultEndpoints.Onboarding;
}

// Creating the first entry is an onboarding milestone owned by Identity — Vault pushes it as a command,
// never a cross-module read of Identity's state. Only the initial write counts.
[UsedImplicitly]
internal sealed class OnEntryCreatedOnboarding(IPublishEndpoint publishEndpoint) : IConsumer<EntryUpsertedEvent>
{
    public Task Consume(ConsumeContext<EntryUpsertedEvent> context)
    {
        var msg = context.Message;
        if (msg.Change != EntityChange.Created)
        {
            return Task.CompletedTask;
        }

        return publishEndpoint.Publish(
            new MarkOnboardingStepCommand(msg.UserId, OnboardingStep.EntryCreated), context.CancellationToken);
    }
}
