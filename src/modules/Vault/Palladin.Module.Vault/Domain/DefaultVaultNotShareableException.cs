using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// The default (personal) vault is private to its owner and cannot gain additional user members.
// Agent grants are unaffected — this only blocks sharing the vault with other users.
// Mapped to HTTP 409 via ConflictException.
internal sealed class DefaultVaultNotShareableException()
    : ConflictException("The default vault cannot be shared with other users.");
