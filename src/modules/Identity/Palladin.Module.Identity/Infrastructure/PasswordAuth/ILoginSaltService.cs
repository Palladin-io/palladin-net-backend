namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

internal interface ILoginSaltService
{
    // Returns the public KDF bootstrap for a matching password account, or deterministic pseudo-values
    // for any unknown / non-password email so the two cases are indistinguishable.
    Task<LoginBootstrap> GetBootstrapAsync(string normalizedEmail, string profileId, CancellationToken ct);
}

internal sealed record LoginBootstrap(Guid AccountId, byte[] KdfSalt);
