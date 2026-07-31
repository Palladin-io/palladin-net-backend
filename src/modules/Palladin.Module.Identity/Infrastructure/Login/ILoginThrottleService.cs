using NodaTime;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal interface ILoginThrottleService
{
    Task<bool> IsLockedAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct);

    Task RecordFailureAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct);

    // Clears the lockout within the CURRENT unit of work but does NOT commit — the caller commits it
    // together with session issuance / challenge consumption so a partial failure can't leave a
    // consumed challenge without a session.
    Task ResetAsync(string normalizedEmail, string ipAddress, Instant now, CancellationToken ct);
}
