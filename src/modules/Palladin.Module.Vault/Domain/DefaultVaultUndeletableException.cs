using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// The user's default (personal) vault is an onboarding invariant and cannot be deleted.
// Mapped to HTTP 409 via ConflictException.
internal sealed class DefaultVaultUndeletableException()
    : ConflictException("The default vault cannot be deleted.");
