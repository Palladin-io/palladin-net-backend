using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record ApiKeyDeletedEvent(
    Guid ApiKeyId,
    Guid OrganizationId,
    string Name,
    string KeySuffix,
    Guid DeletedBy,
    string DeletedByName,
    Instant DeletedAt) : IIntegrationEvent;
