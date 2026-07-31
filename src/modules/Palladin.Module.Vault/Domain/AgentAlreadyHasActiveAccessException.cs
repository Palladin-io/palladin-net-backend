using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// Thrown when creating an active grant would violate the coverage invariant: an agent has AT MOST ONE
// active grant covering a given entry (active GRANULAR on the entry, or active FULL on the vault).
// Mapped to HTTP 409 via ConflictException.
internal sealed class AgentAlreadyHasActiveAccessException(string scope)
    : ConflictException($"Agent already has active access to this {scope}.");
