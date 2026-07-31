using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal static class ExpirySource
{
    internal const string Time = "time";
    internal const string Uses = "uses";
    internal const string Lifetime = "lifetime";

    // At most one of (expiresAt, queryLimit) is set — both being set is rejected at validation.
    // Neither set => Lifetime (never expires).
    internal static string From(Instant? expiresAt, int? queryLimit) =>
        expiresAt.HasValue ? Time
        : queryLimit.HasValue ? Uses
        : Lifetime;
}
