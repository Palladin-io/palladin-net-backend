using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// A user may own at most one default (personal) vault. Enforced at the create endpoint and by a
// filtered unique index on the Vaults table. Mapped to HTTP 409 via ConflictException.
internal sealed class DefaultVaultAlreadyExistsException()
    : ConflictException("A default vault already exists for this user.");
