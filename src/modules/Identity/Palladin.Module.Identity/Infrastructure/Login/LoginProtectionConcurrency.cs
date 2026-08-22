using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal static class LoginProtectionConcurrency
{
    internal static bool IsRetryable(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is DbUpdateException
        {
            InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation },
        };
}
