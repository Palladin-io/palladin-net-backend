namespace Palladin.Module.Vault.Domain;

internal sealed class RoleVaultAccessPolicy
{
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid VaultId { get; private set; }

    public RoleVaultAccessPolicySet PolicySet { get; private set; } = null!;
    public Vault Vault { get; private set; } = null!;

    private RoleVaultAccessPolicy() { }

    internal static RoleVaultAccessPolicy Create(Guid organizationId, Guid roleId, Guid vaultId) =>
        new()
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            VaultId = vaultId,
        };
}
