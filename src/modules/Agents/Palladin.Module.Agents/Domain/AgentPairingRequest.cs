using NodaTime;

namespace Palladin.Module.Agents.Domain;

internal sealed class AgentPairingRequest
{
    public Guid Id { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string PublicKey { get; private set; } = string.Empty;
    public string SigningPublicKey { get; private set; } = string.Empty;
    public string? RequestedDisplayName { get; private set; }
    public string? RequestedType { get; private set; }
    public string? Hostname { get; private set; }
    public string? Ip { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Type { get; private set; }
    public string? ReservedDisplayName { get; private set; }
    public string? ReservedDisplayNameKey { get; private set; }
    public AgentPairingStatus Status { get; private set; }
    public Guid? AgentId { get; private set; }
    public Guid? ApiKeyId { get; private set; }
    public string? CredentialSuite { get; private set; }
    public string? CredentialEphemeralPublicKey { get; private set; }
    public string? CredentialNonce { get; private set; }
    public string? CredentialCiphertext { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public uint Revision { get; private set; }

    private AgentPairingRequest() { }

    internal static AgentPairingRequest Create(
        Guid id,
        string publicKey,
        string signingPublicKey,
        string? displayName,
        string? type,
        Instant now,
        Instant expiresAt,
        string? hostname = null,
        string? ip = null) =>
        new()
        {
            Id = id,
            PublicKey = publicKey,
            SigningPublicKey = signingPublicKey,
            RequestedDisplayName = displayName,
            RequestedType = type,
            Hostname = hostname,
            Ip = ip,
            DisplayName = displayName,
            Type = type,
            Status = AgentPairingStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            Revision = 1,
        };

    internal bool IsExpired(Instant now) => now >= ExpiresAt;

    internal bool TryClaim(Guid organizationId, Instant now)
    {
        if (Status != AgentPairingStatus.Pending || IsExpired(now))
        {
            return false;
        }

        if (OrganizationId is not null && OrganizationId != organizationId)
        {
            return false;
        }

        OrganizationId = organizationId;
        UpdatedAt = now;
        AdvanceRevision();
        return true;
    }

    internal bool TryReserveDisplayName(Guid organizationId, string displayName, Instant now)
    {
        if (OrganizationId != organizationId || Status != AgentPairingStatus.Pending || IsExpired(now))
        {
            return false;
        }

        ReservedDisplayName = displayName;
        ReservedDisplayNameKey = AgentMetadata.DisplayNameReservationKey(displayName);
        UpdatedAt = now;
        AdvanceRevision();
        return true;
    }

    internal bool TryApprove(
        Guid organizationId,
        Guid agentId,
        Guid apiKeyId,
        string displayName,
        string? type,
        ApiKeyCredentialEnvelope envelope,
        Instant now)
    {
        if (OrganizationId != organizationId
            || Status != AgentPairingStatus.Pending
            || IsExpired(now)
            || ReservedDisplayName != displayName)
        {
            return false;
        }

        AgentId = agentId;
        ApiKeyId = apiKeyId;
        DisplayName = displayName;
        Type = type;
        CredentialSuite = envelope.Suite;
        CredentialEphemeralPublicKey = envelope.EphemeralPublicKey;
        CredentialNonce = envelope.Nonce;
        CredentialCiphertext = envelope.Ciphertext;
        ReservedDisplayName = null;
        ReservedDisplayNameKey = null;
        Status = AgentPairingStatus.Approved;
        UpdatedAt = now;
        AdvanceRevision();
        return true;
    }

    internal bool TryReject(Guid organizationId, Instant now)
    {
        if (OrganizationId != organizationId || Status != AgentPairingStatus.Pending || IsExpired(now))
        {
            return false;
        }

        ReservedDisplayName = null;
        ReservedDisplayNameKey = null;
        Status = AgentPairingStatus.Rejected;
        UpdatedAt = now;
        AdvanceRevision();
        return true;
    }

    internal bool TryExpire(Instant now)
    {
        if (Status != AgentPairingStatus.Pending || !IsExpired(now))
        {
            return false;
        }

        ReservedDisplayName = null;
        ReservedDisplayNameKey = null;
        Status = AgentPairingStatus.Expired;
        UpdatedAt = now;
        AdvanceRevision();
        return true;
    }

    private void AdvanceRevision() => Revision = checked(Revision + 1);
}

internal sealed record ApiKeyCredentialEnvelope(
    string Suite,
    string EphemeralPublicKey,
    string Nonce,
    string Ciphertext);

internal enum AgentPairingStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Expired = 4,
}
