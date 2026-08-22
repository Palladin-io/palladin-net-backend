using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// EmailHash is the analytics distinct id. Recipient fields are populated only for a known account
// and exist solely for the security email; analytics must never receive them or the source IP.
[PublicAPI]
public sealed record LoginLockedOutEvent(
    Guid LockoutOccurrenceId,
    string EmailHash,
    string IpAddress,
    Guid? TargetUserId,
    string? RecipientEmail,
    string? PreferredLanguage,
    int AttemptCount,
    int WindowMinutes,
    int LockoutMinutes,
    Instant LockedUntil,
    Instant OccurredAt) : IIntegrationEvent;
