using JetBrains.Annotations;

namespace Palladin.Module.Identity.Shared;

// Issued by register, login (success) and the TOTP login step. The client then calls GET api/account
// for the crypto material and derives the master key locally — the server never sees it.
[PublicAPI]
public sealed record AuthSessionResponse(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    bool IsOnboarded,
    bool EmailVerified);
