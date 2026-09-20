using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareSenderAuthority
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public uint RevokedThroughAuthorizationVersion { get; private set; }
    public long MutationVersion { get; private set; }

    private EntryShareSenderAuthority() { }

    internal static EntryShareSenderAuthority Create(Guid organizationId, Guid userId)
    {
        if (organizationId == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("Sharing authority requires an organization and a Member.");
        }

        return new EntryShareSenderAuthority
        {
            OrganizationId = organizationId,
            UserId = userId,
            MutationVersion = 1,
        };
    }

    internal void RevokeThrough(uint authorizationVersion)
    {
        if (authorizationVersion > RevokedThroughAuthorizationVersion)
        {
            RevokedThroughAuthorizationVersion = authorizationVersion;
            MutationVersion = checked(MutationVersion + 1);
        }
    }

    internal bool Allows(uint authorizationVersion) =>
        authorizationVersion > RevokedThroughAuthorizationVersion;
}
