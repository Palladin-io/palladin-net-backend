using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal enum RoleVaultAccessOperationStatus
{
    AwaitingProvisioner = 1,
    Superseded = 2,
}

internal sealed class RoleVaultAccessOperation
{
    public Guid OrganizationId { get; private set; }
    public Guid Id { get; private set; }
    public Guid RoleId { get; private set; }
    public RoleVaultAccessOperationStatus Status { get; private set; }
    public int AddsAwaitingProvisioning { get; private set; }
    public int RemovalsAwaitingSourceReconciliation { get; private set; }
    public int Unchanged { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private RoleVaultAccessOperation() { }

    internal static RoleVaultAccessOperation Create(
        Guid organizationId,
        Guid id,
        Guid roleId,
        int addsAwaitingProvisioning,
        int removalsAwaitingSourceReconciliation,
        int unchanged,
        Guid createdBy,
        Instant now) =>
        new()
        {
            OrganizationId = organizationId,
            Id = id,
            RoleId = roleId,
            Status = RoleVaultAccessOperationStatus.AwaitingProvisioner,
            AddsAwaitingProvisioning = addsAwaitingProvisioning,
            RemovalsAwaitingSourceReconciliation = removalsAwaitingSourceReconciliation,
            Unchanged = unchanged,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Supersede(Instant now)
    {
        if (Status == RoleVaultAccessOperationStatus.Superseded)
        {
            return;
        }

        Status = RoleVaultAccessOperationStatus.Superseded;
        UpdatedAt = now;
    }
}
