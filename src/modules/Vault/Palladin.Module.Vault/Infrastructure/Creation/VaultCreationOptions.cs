namespace Palladin.Module.Vault.Infrastructure.Creation;

internal sealed class VaultCreationOptions
{
    internal const string Position = "Modules:Vault:Creation";

    public int ChallengeTtlSeconds { get; init; } = 600;
}
