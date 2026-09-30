using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;

namespace Palladin.Module.Notification.Features;

// Fail closed at the Notification module boundary. Vault publishers may only send opaque identifiers
// and structural facts; presentation data is resolved by an unlocked client from its local index.
internal static class OpaqueVaultNotificationPolicy
{
    private static readonly HashSet<NotificationType> VaultTypes =
    [
        NotificationType.GrantPending,
        NotificationType.GrantApproved,
        NotificationType.GrantDenied,
        NotificationType.GrantRevoked,
        NotificationType.CredentialStale,
    ];

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "requestId",
        "grantId",
        "entityId",
        "vaultId",
        "entryId",
        "agentId",
        "methods",
        "grantType",
        "queryLimit",
        "queryCount",
        "expiresAt",
        "durationActiveSeconds",
        "bySystem",
        "errorHint",
        "actionType",
    };

    private static readonly HashSet<string> OpaqueIdKeys = new(StringComparer.Ordinal)
    {
        "requestId",
        "grantId",
        "entityId",
        "vaultId",
        "entryId",
        "agentId",
    };

    internal static void EnsureSafe(BroadcastNotificationCommand command)
    {
        if (command.Type == NotificationType.EntryShareReceived)
        {
            throw new InvalidOperationException("Sharing receipts require the explicit sender-only receipt command.");
        }

        if (!VaultTypes.Contains(command.Type))
        {
            return;
        }

        foreach (var (key, value) in command.Metadata)
        {
            if (!AllowedKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    "The Vault notification contains metadata outside the structural allowlist.");
            }

            if (OpaqueIdKeys.Contains(key) && !Guid.TryParse(value, out _))
            {
                throw new InvalidOperationException(
                    $"Metadata key '{key}' must contain an opaque identifier.");
            }
        }
    }
}
