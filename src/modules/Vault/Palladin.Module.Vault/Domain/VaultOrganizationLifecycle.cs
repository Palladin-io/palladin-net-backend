using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultOrganizationLifecycle
{
    internal Guid OrganizationId { get; private set; }
    internal ulong MutationVersion { get; private set; }
    internal bool SharingDisabled { get; private set; }

    private VaultOrganizationLifecycle() { }

    internal static VaultOrganizationLifecycle Create(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("Vault organization lifecycle requires an organization identifier.");
        }

        return new VaultOrganizationLifecycle
        {
            OrganizationId = organizationId,
            MutationVersion = 1,
        };
    }

    internal void FenceMutation()
    {
        if (MutationVersion == ulong.MaxValue)
        {
            throw new DomainException("Vault organization lifecycle version namespace is exhausted.");
        }

        MutationVersion++;
    }

    internal void DisableSharing()
    {
        if (!SharingDisabled)
        {
            SharingDisabled = true;
            FenceMutation();
        }
    }
}
