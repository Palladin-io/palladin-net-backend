using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Events;
using Palladin.Module.Agents.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Agents.Domain;

internal sealed class ApiKey : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string KeyHash { get; private set; } = string.Empty;
    public string KeySuffix { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public Guid CreatedBy { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }
    public Instant? RevokedAt { get; private set; }
    public ApiKeyStatus Status { get; private set; }

    private ApiKey() { }

    internal static (ApiKey Entity, string Plaintext) Generate(Guid id, Guid organizationId, string name, Guid createdBy, string createdByName, Instant now)
    {
        var random = RandomNumberGenerator.GetBytes(32);
        var plaintext = "pl_" + Convert.ToBase64String(random)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var entity = new ApiKey
        {
            Id = id,
            OrganizationId = organizationId,
            KeyHash = HashKey(plaintext),
            KeySuffix = plaintext[^4..],
            Name = name,
            CreatedBy = createdBy,
            CreatedAt = now,
            Status = ApiKeyStatus.Active,
        };

        entity.AddEvent(new ApiKeyCreatedEvent(entity.Id, organizationId, name, entity.KeySuffix, createdBy, createdByName, now));

        return (entity, plaintext);
    }

    internal static string HashKey(string plaintext)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal void Revoke(Guid revokedBy, string revokedByName, Instant now)
    {
        RevokedAt = now;
        RevokedBy = revokedBy;
        Status = ApiKeyStatus.Revoked;

        AddEvent(new ApiKeyRevokedEvent(Id, OrganizationId, Name, KeySuffix, revokedBy, revokedByName, now));
    }

    internal void Activate(Guid activatedBy, string activatedByName, Instant now)
    {
        RevokedAt = null;
        RevokedBy = null;
        Status = ApiKeyStatus.Active;

        AddEvent(new ApiKeyActivatedEvent(Id, OrganizationId, Name, KeySuffix, activatedBy, activatedByName, now));
    }

    internal void MarkDeleted(Guid deletedBy, string deletedByName, Instant now) =>
        AddEvent(new ApiKeyDeletedEvent(Id, OrganizationId, Name, KeySuffix, deletedBy, deletedByName, now));
}

public enum ApiKeyStatus
{
    Active = 1,
    Revoked = 2,
}
