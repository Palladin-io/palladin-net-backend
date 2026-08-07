using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record ApiKeyActivatedEvent(
    Guid ApiKeyId,
    Guid OrganizationId,
    string Name,
    string KeySuffix,
    Guid ActivatedBy,
    string ActivatedByName,
    Instant UpdatedAt) : IIntegrationEvent;
