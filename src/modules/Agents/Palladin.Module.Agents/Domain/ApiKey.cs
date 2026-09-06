using System.Buffers;
using System.Buffers.Text;
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
    public long Revision { get; private set; } = 1;

    private ApiKey() { }

    internal static (ApiKey Entity, string Plaintext) Generate(Guid id, Guid organizationId, string name, Guid createdBy, string createdByName, Instant now)
    {
        using var secret = GeneratedApiKeySecret.Generate();
        return (Create(id, organizationId, name, createdBy, createdByName, now, secret.Bytes),
            Encoding.ASCII.GetString(secret.Bytes));
    }

    internal static ApiKey GenerateHidden(
        Guid id,
        Guid organizationId,
        string name,
        Guid createdBy,
        string createdByName,
        Instant now)
    {
        using var secret = GeneratedApiKeySecret.Generate();
        return Create(id, organizationId, name, createdBy, createdByName, now, secret.Bytes);
    }

    internal static string HashKey(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var hash = SHA256.HashData(plaintextBytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    internal static string HashKey(ReadOnlySpan<byte> plaintext)
    {
        var hash = SHA256.HashData(plaintext);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static ApiKey Create(
        Guid id,
        Guid organizationId,
        string name,
        Guid createdBy,
        string createdByName,
        Instant now,
        ReadOnlySpan<byte> secret)
    {
        var entity = new ApiKey
        {
            Id = id,
            OrganizationId = organizationId,
            KeyHash = HashKey(secret),
            KeySuffix = Encoding.ASCII.GetString(secret[^4..]),
            Name = name,
            CreatedBy = createdBy,
            CreatedAt = now,
            Status = ApiKeyStatus.Active,
        };

        entity.AddEvent(new ApiKeyCreatedEvent(entity.Id, organizationId, name, entity.KeySuffix, createdBy, createdByName, now));
        return entity;
    }

    internal void Revoke(Guid revokedBy, string revokedByName, Instant now)
    {
        RevokedAt = now;
        RevokedBy = revokedBy;
        Status = ApiKeyStatus.Revoked;
        Revision++;

        AddEvent(new ApiKeyRevokedEvent(Id, OrganizationId, Name, KeySuffix, revokedBy, revokedByName, now));
    }

    internal void Activate(Guid activatedBy, string activatedByName, Instant now)
    {
        RevokedAt = null;
        RevokedBy = null;
        Status = ApiKeyStatus.Active;
        Revision++;

        AddEvent(new ApiKeyActivatedEvent(Id, OrganizationId, Name, KeySuffix, activatedBy, activatedByName, now));
    }

    internal void MarkDeleted(Guid deletedBy, string deletedByName, Instant now) =>
        AddEvent(new ApiKeyDeletedEvent(Id, OrganizationId, Name, KeySuffix, deletedBy, deletedByName, now));

    internal bool TryFenceAgentPairingApproval()
    {
        if (Status != ApiKeyStatus.Active)
        {
            return false;
        }

        Revision++;
        return true;
    }
}

internal sealed class GeneratedApiKeySecret : IDisposable
{
    private byte[] bytes;

    private GeneratedApiKeySecret(byte[] bytes) => this.bytes = bytes;

    internal ReadOnlySpan<byte> Bytes => bytes;

    internal static GeneratedApiKeySecret Generate()
    {
        Span<byte> random = stackalloc byte[32];
        Span<byte> encoded = stackalloc byte[44];
        try
        {
            RandomNumberGenerator.Fill(random);
            var status = Base64.EncodeToUtf8(random, encoded, out var consumed, out var written);
            if (status != OperationStatus.Done || consumed != random.Length || written != encoded.Length)
            {
                throw new CryptographicException("API key generation failed.");
            }

            while (written > 0 && encoded[written - 1] == (byte)'=')
            {
                written--;
            }

            var secret = GC.AllocateUninitializedArray<byte>(3 + written);
            "pl_"u8.CopyTo(secret);
            encoded[..written].CopyTo(secret.AsSpan(3));
            for (var i = 3; i < secret.Length; i++)
            {
                secret[i] = secret[i] switch
                {
                    (byte)'+' => (byte)'-',
                    (byte)'/' => (byte)'_',
                    _ => secret[i],
                };
            }

            return new GeneratedApiKeySecret(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(bytes);
        bytes = [];
    }
}

public enum ApiKeyStatus
{
    Active = 1,
    Revoked = 2,
}
