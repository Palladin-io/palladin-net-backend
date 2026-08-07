using Palladin.Core.Events;
using Palladin.Module.Vault.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultMember : EventEntityBase
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid UserId { get; private set; }
    public Instant AddedAt { get; private set; }

    public Vault Vault { get; private set; } = null!;

    private VaultMember() { }

    internal static VaultMember Create(
        Guid organizationId,
        Guid vaultId,
        Guid userId,
        Instant now)
    {
        var member = new VaultMember
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            UserId = userId,
            AddedAt = now,
        };

        member.AddEvent(new VaultMemberAddedEvent(organizationId, vaultId, userId, now));

        return member;
    }
}
