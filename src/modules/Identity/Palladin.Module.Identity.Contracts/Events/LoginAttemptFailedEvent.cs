using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record LoginAttemptFailedEvent(
    Guid AttemptId,
    string EmailHash,
    string IpAddress,
    Guid? OrganizationId,
    Guid? TargetUserId,
    string Factor,
    Instant OccurredAt) : IIntegrationEvent;
