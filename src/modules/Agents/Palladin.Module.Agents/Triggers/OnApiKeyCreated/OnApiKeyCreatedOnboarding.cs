using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Identity.Contracts.Commands;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnApiKeyCreatedOnboardingDefinition : ConsumerDefinition<OnApiKeyCreatedOnboarding>
{
    public OnApiKeyCreatedOnboardingDefinition() => EndpointName = AgentsEndpoints.Onboarding;
}

// Creating an API key is an ORG-level onboarding milestone — once anyone in the org has one, it is done
// for everyone. Push it to Identity as an org command, never a cross-module read.
[UsedImplicitly]
internal sealed class OnApiKeyCreatedOnboarding(IPublishEndpoint publishEndpoint) : IConsumer<ApiKeyCreatedEvent>
{
    public Task Consume(ConsumeContext<ApiKeyCreatedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(
            new MarkOrganizationOnboardingStepCommand(msg.OrganizationId, OnboardingStep.ApiKeyCreated),
            context.CancellationToken);
    }
}
