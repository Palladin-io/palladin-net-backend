using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

internal interface ILoginSaltService
{
    // Returns the account's own registered KDF profile and public bootstrap, or deterministic current-profile
    // pseudo-values for any unknown / non-password email.
    Task<LoginBootstrap> GetBootstrapAsync(string normalizedEmail, CancellationToken ct);
}

internal sealed record LoginBootstrap(Guid AccountId, byte[] KdfSalt, IdentityKdfProfile Profile);
