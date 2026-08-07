namespace Palladin.Module.Identity.Domain.Enums;

// Single-use, hashed, TTL-bound tokens. Every lookup MUST filter by Purpose so a token minted for
// one flow can never be redeemed in another.
public enum VerificationTokenPurpose
{
    EmailVerify = 1,
    EmailChange = 2,
    LoginTotpChallenge = 3,
}
