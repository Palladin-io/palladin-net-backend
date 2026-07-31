using NodaTime;

namespace Palladin.Module.Vault.Infrastructure.Persistence;

internal static class PostgreSqlInstant
{
    internal static Instant Normalize(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - ticks % 10);
    }
}
