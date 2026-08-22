using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Identity.Contracts.Commands;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentEnrolledOnboardingDefinition : ConsumerDefinition<OnAgentEnrolledOnboarding>
{
    public OnAgentEnrolledOnboardingDefinition() => EndpointName = AgentsEndpoints.Self;
}

// Enrolling an agent (agent becomes Active) is an ORG-level onboarding milestone — once anyone in the
// org has an active agent, it is done for everyone. Push it to Identity as an org command; the event
// carries the org, so no read-model lookup is needed.
[UsedImplicitly]
internal sealed class OnAgentEnrolledOnboarding(IPublishEndpoint publishEndpoint) : IConsumer<AgentUpsertedEvent>
{
    public Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        if (msg.Status != AgentStatus.Active)
        {
            return Task.CompletedTask;
        }

        return publishEndpoint.Publish(
            new MarkOrganizationOnboardingStepCommand(msg.OrganizationId, OnboardingStep.AgentEnrolled),
            context.CancellationToken);
    }
}
