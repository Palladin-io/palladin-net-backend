using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultKeyRotationReason
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid RotationId { get; private set; }
    internal VaultPrincipalType PrincipalType { get; private set; }
    public Guid PrincipalId { get; private set; }
    public string? PrincipalName { get; private set; }
    public Instant OccurredAt { get; private set; }

    private VaultKeyRotationReason() { }

    internal static VaultKeyRotationReason Create(
        VaultKeyRotation rotation,
        VaultPrincipalType principalType,
        Guid principalId,
        string? principalName,
        Instant occurredAt) => new()
        {
            OrganizationId = rotation.OrganizationId,
            VaultId = rotation.VaultId,
            RotationId = rotation.Id,
            PrincipalType = principalType,
            PrincipalId = principalId,
            PrincipalName = principalName,
            OccurredAt = occurredAt,
        };
}
