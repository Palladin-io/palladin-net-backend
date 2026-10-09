using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public sealed record BrowserAuthSessionResponse(string AccessToken, Guid SessionId, Guid UserId,
    bool IsOnboarded, bool EmailVerified, Instant? WaitlistDeveloperBenefitStartedAt,
    Instant? WaitlistDeveloperBenefitEndsAt);
