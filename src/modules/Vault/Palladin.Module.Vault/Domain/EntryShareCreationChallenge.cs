using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryShareCreationChallenge
{
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public Guid ShareId { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public long MutationVersion { get; private set; }

    private EntryShareCreationChallenge() { }

    internal static EntryShareCreationChallenge Create(
        EntryScope source, Guid requestedBy, Guid shareId, Instant expiresAt)
    {
        source.Validate();
        if (requestedBy == Guid.Empty || shareId == Guid.Empty)
        {
            throw new DomainException("A sharing reservation must identify its Member and share.");
        }

        return new EntryShareCreationChallenge
        {
            OrganizationId = source.OrganizationId,
            VaultId = source.VaultId,
            EntryId = source.EntryId,
            RequestedBy = requestedBy,
            ShareId = shareId,
            ExpiresAt = expiresAt,
            MutationVersion = 1,
        };
    }

    internal void Renew(Guid shareId, Instant now, Duration lifetime)
    {
        if (shareId == Guid.Empty || now < ExpiresAt || lifetime <= Duration.Zero)
        {
            throw new DomainException("Only an expired sharing reservation can be renewed.");
        }

        ShareId = shareId;
        ExpiresAt = now + lifetime;
        MutationVersion = checked(MutationVersion + 1);
    }

    internal void Validate(Guid shareId, EntryScope source, Guid memberId, Instant now)
    {
        if (shareId != ShareId || source.OrganizationId != OrganizationId
            || source.VaultId != VaultId || source.EntryId != EntryId
            || memberId != RequestedBy || now >= ExpiresAt)
        {
            throw new EntryShareUnavailableException();
        }
    }
}
