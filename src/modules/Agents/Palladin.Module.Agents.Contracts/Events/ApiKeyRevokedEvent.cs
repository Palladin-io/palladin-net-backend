using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Agents.Contracts.Events;

[PublicAPI]
public sealed record ApiKeyRevokedEvent(
    Guid ApiKeyId,
    Guid OrganizationId,
    string Name,
    string KeySuffix,
    Guid RevokedBy,
    string RevokedByName,
    Instant UpdatedAt) : IIntegrationEvent;
