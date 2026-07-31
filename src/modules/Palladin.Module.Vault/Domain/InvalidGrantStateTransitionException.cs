using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// Thrown when a user-initiated grant state transition (approve / deny / revoke) is attempted from an
// incompatible status. Fail-fast so a business-logic error surfaces immediately (mapped to 409 via
// ConflictException). System/batch paths (cascade revoke, expiry cron) do NOT throw — they stay
// idempotent no-ops.
internal sealed class InvalidGrantStateTransitionException(GrantStatusTransition transition, Core.Types.GrantStatus currentStatus)
    : ConflictException($"Cannot {transition} a grant in status {currentStatus}.");

internal enum GrantStatusTransition
{
    Approve = 1,
    Deny = 2,
    Revoke = 3,
}
