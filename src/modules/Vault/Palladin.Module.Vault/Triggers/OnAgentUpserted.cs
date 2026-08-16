using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentUpsertedDefinition : ConsumerDefinition<OnAgentUpserted>
{
    public OnAgentUpsertedDefinition() => EndpointName = VaultEndpoints.FromAgents;
}

[UsedImplicitly]
internal sealed class OnAgentUpserted(
    VaultDomainWriteContext domainWriteContext) : IConsumer<AgentUpsertedEvent>
{
    public async Task Consume(ConsumeContext<AgentUpsertedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var updatedAt = PostgreSqlInstant.Normalize(msg.UpdatedAt);
        var accessEpochStartedAt = msg.AccessEpochStartedAt is { } accessEpoch
            ? PostgreSqlInstant.Normalize(accessEpoch)
            : (Instant?)null;
        var hasValidAccessEpoch = msg.Status switch
        {
            AgentStatus.Pending => msg.AccessEpoch == 0,
            AgentStatus.Active => msg.AccessEpoch > 0,
            AgentStatus.Deactivated => true,
            _ => false,
        };
        if (!hasValidAccessEpoch
            || (msg.Status == AgentStatus.Active) != (accessEpochStartedAt is not null)
            || accessEpochStartedAt > updatedAt)
        {
            throw new InvalidOperationException(
                "Agent lifecycle event must carry a valid access epoch exactly when active.");
        }

        var existing = await domainWriteContext.Agents.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId && x.Id == msg.AgentId,
            ct);

        if (existing is null)
        {
            domainWriteContext.Add(Agent.Create(
                msg.AgentId,
                msg.OrganizationId,
                msg.Status,
                msg.PublicKey,
                msg.RecipientKeyVersion,
                msg.SigningPublicKey,
                msg.Name,
                msg.IconKey,
                msg.IconColor,
                msg.AccessEpoch,
                accessEpochStartedAt,
                updatedAt));
            await domainWriteContext.CommitAsync(ct);
            return;
        }

        if (msg.AccessEpoch < existing.AccessEpoch
            || (msg.AccessEpoch == existing.AccessEpoch && updatedAt <= existing.UpdatedAt))
        {
            return;
        }

        existing.Apply(
            msg.Status,
            msg.PublicKey,
            msg.RecipientKeyVersion,
            msg.SigningPublicKey,
            msg.Name,
            msg.IconKey,
            msg.IconColor,
            msg.AccessEpoch,
            accessEpochStartedAt,
            updatedAt);
        await domainWriteContext.CommitAsync(ct);
    }
}
