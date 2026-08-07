using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberRemovalCompletedEvent(
    Guid RequestId,
    Guid OrganizationId,
    Guid UserId,
    Instant CompletedAt,
    Instant UpdatedAt) : IIntegrationEvent;
