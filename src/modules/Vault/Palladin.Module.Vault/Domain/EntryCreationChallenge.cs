using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class EntryCreationChallenge
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid EntryId { get; private set; }
    internal Guid RequestedBy { get; private set; }
    internal Instant CreatedAt { get; private set; }
    internal Instant ExpiresAt { get; private set; }
    internal Instant? ConsumedAt { get; private set; }

    private EntryCreationChallenge() { }

    internal static EntryCreationChallenge Create(
        EntryScope scope,
        Guid requestedBy,
        Instant createdAt,
        Duration lifetime)
    {
        scope.Validate();
        if (requestedBy == Guid.Empty || lifetime <= Duration.Zero)
        {
            throw new DomainException("Entry creation challenge identity and lifetime must be valid.");
        }

        return new EntryCreationChallenge
        {
            OrganizationId = scope.OrganizationId,
            VaultId = scope.VaultId,
            EntryId = scope.EntryId,
            RequestedBy = requestedBy,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + lifetime,
        };
    }

    internal void Consume(EntryScope scope, Guid requestedBy, Instant now)
    {
        if (OrganizationId != scope.OrganizationId
            || VaultId != scope.VaultId
            || EntryId != scope.EntryId
            || RequestedBy != requestedBy)
        {
            throw new DomainException("Entry creation challenge scope does not match the authenticated Member.");
        }

        if (ConsumedAt is not null)
        {
            throw new DomainException("Entry creation challenge has already been consumed.");
        }

        if (now >= ExpiresAt)
        {
            throw new DomainException("Entry creation challenge has expired.");
        }

        ConsumedAt = now;
    }

    internal bool IsActiveAt(Instant now) => ConsumedAt is null && now < ExpiresAt;
}
