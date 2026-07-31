using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultCreationChallenge
{
    internal Guid OrganizationId { get; private set; }
    internal Guid VaultId { get; private set; }
    internal Guid RequestedBy { get; private set; }
    internal Instant CreatedAt { get; private set; }
    internal Instant ExpiresAt { get; private set; }
    internal Instant? ConsumedAt { get; private set; }

    private VaultCreationChallenge() { }

    internal static VaultCreationChallenge Create(
        Guid organizationId,
        Guid vaultId,
        Guid requestedBy,
        Instant createdAt,
        Duration lifetime)
    {
        new VaultScope(organizationId, vaultId).Validate();

        if (requestedBy == Guid.Empty || lifetime <= Duration.Zero)
        {
            throw new DomainException("Vault creation challenge identity and lifetime must be valid.");
        }

        return new VaultCreationChallenge
        {
            OrganizationId = organizationId,
            VaultId = vaultId,
            RequestedBy = requestedBy,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + lifetime,
        };
    }

    internal void Consume(Guid organizationId, Guid requestedBy, Instant now)
    {
        if (OrganizationId != organizationId || RequestedBy != requestedBy)
        {
            throw new DomainException("Vault creation challenge scope does not match the authenticated Member.");
        }

        if (ConsumedAt is not null)
        {
            throw new DomainException("Vault creation challenge has already been consumed.");
        }

        if (now >= ExpiresAt)
        {
            throw new DomainException("Vault creation challenge has expired.");
        }

        ConsumedAt = now;
    }

    internal bool IsActiveAt(Instant now) => ConsumedAt is null && now < ExpiresAt;
}
