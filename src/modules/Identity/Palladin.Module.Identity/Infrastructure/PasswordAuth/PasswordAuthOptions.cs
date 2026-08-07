using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

// Leaf Position; the "Modules:Identity" prefix is composed at registration.
[UsedImplicitly]
internal sealed class PasswordAuthOptions
{
    public const string Position = "PasswordAuth";

    // Server-side Argon2id parameters used to re-hash the client authHash at rest.
    public int MemoryKib { get; init; } = 19456;
    public int Iterations { get; init; } = 2;
    public int DegreeOfParallelism { get; init; } = 1;
    public int HashLength { get; init; } = 32;
    public int ServerSaltLength { get; init; } = 16;

    // Length of the deterministic pseudo-salt returned for unknown emails; must match the client salt
    // length so known and unknown accounts are indistinguishable on login/salt.
    public int PseudoSaltLength { get; init; } = 16;

    // HMAC key for the deterministic pseudo-salt. Never logged. Distinct from the JWT secret.
    public string EnumerationSecret { get; init; } = string.Empty;
}
