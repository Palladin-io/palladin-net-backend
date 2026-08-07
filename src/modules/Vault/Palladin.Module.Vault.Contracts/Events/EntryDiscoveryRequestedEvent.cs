using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// An agent ran an entry-discovery search across its organization. Consumed by Analytics.
// Carries the result count (not the query text — discovery queries can include sensitive fragments).
[PublicAPI]
public sealed record EntryDiscoveryRequestedEvent(
    Guid AgentId,
    Guid OrganizationId,
    int ResultCount,
    Instant UpdatedAt) : IIntegrationEvent;
