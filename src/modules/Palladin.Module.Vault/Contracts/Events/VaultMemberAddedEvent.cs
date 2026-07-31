using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

// Emitted whenever a user becomes a member of a vault. Currently this occurs when the owner membership
// is created; future vault-sharing support will use the same event. Search consumes it to scope results to vaults a
// user may see — never leaking vaults/entries the caller is not a member of.
[PublicAPI]
public sealed record VaultMemberAddedEvent(
    Guid OrganizationId,
    Guid VaultId,
    Guid UserId,
    Instant AddedAt) : IIntegrationEvent;
