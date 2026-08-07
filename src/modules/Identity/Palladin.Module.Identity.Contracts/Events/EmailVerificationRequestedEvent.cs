using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// Carries the plaintext verification token to the email trigger (only the hash is persisted),
// mirroring how WaitlistJoinedEvent delivers its token. Never logged.
[PublicAPI]
public sealed record EmailVerificationRequestedEvent(
    Guid UserId,
    string Email,
    string Language,
    string Token,
    int ExpiryMinutes,
    Instant OccurredAt) : IIntegrationEvent;
