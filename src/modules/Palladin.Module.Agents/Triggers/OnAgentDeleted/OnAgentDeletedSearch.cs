using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentDeletedSearchDefinition : ConsumerDefinition<OnAgentDeletedSearch>
{
    public OnAgentDeletedSearchDefinition() => EndpointName = AgentsEndpoints.Search;
}

[UsedImplicitly]
internal sealed class OnAgentDeletedSearch(IPublishEndpoint publishEndpoint) : IConsumer<AgentDeletedEvent>
{
    public Task Consume(ConsumeContext<AgentDeletedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(
            new RemoveSearchItemCommand(msg.OrganizationId, msg.AgentId, SearchItemTypes.Agent, msg.DeletedAt),
            context.CancellationToken);
    }
}
