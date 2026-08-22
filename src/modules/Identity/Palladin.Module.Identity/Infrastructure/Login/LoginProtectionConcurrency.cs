using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Palladin.Module.Identity.Infrastructure.Login;

internal static class LoginProtectionConcurrency
{
    private const string LoginLockoutAccountConstraint = "IX_LoginLockouts_Email";

    internal static bool IsRetryable(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is DbUpdateException
        {
            InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation },
        };

    internal static bool IsAuthenticationFenceConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: LoginLockoutAccountConstraint,
            },
        };
}
