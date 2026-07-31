using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// Agent requested access to a single entry. Consumed by Notification for push/SignalR delivery to
// vault owners and by Audit. The actor is the agent (AgentId); there is no user yet.
[PublicAPI]
public sealed record GrantRequestedEvent(
    Guid GrantId,
    Guid VaultId,
    Guid OrganizationId,
    Guid AgentId,
    Guid EntryId,
    string AgentName,
    string EntryLabel,
    string VaultName,
    GrantMethods RequestedMethods,
    Instant UpdatedAt) : IIntegrationEvent;
