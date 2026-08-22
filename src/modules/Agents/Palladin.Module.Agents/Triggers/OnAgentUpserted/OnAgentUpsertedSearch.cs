using Palladin.Core.Security;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.MassTransit;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using JetBrains.Annotations;
using MassTransit;

namespace Palladin.Module.Agents.Triggers;

[UsedImplicitly]
internal sealed class OnAgentUpsertedSearchDefinition : ConsumerDefinition<OnAgentUpsertedSearch>
{
    public OnAgentUpsertedSearchDefinition() => EndpointName = AgentsEndpoints.Self;
}

// Agents are org-wide (no scope) but gated behind AgentManage, so only members with agent access see
// them in search.
[UsedImplicitly]
internal sealed class OnAgentUpsertedSearch(IPublishEndpoint publishEndpoint) : IConsumer<AgentUpsertedEvent>
{
    public Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        return publishEndpoint.Publish(
            new IndexSearchItemCommand(
                OrganizationId: msg.OrganizationId,
                ItemId: msg.AgentId,
                Type: SearchItemTypes.Agent,
                Name: msg.Name ?? string.Empty,
                SearchTerms: [],
                UpdatedAt: msg.UpdatedAt),
            context.CancellationToken);
    }
}
