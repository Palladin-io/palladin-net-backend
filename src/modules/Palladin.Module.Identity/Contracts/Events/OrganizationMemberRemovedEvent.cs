using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record OrganizationMemberRemovedEvent(
    Guid OrganizationId,
    Guid UserId,
    string UserDisplayName,
    Guid RemovedBy,
    string RemovedByName,
    Instant OccurredAt) : IIntegrationEvent;
