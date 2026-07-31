namespace Palladin.Module.Vault.Infrastructure.Authorization;

internal interface IRequiresVaultMembership
{
    Guid VaultId { get; }
}
