using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Agents.Domain;

internal sealed class Agent : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string PublicKey { get; private set; } = string.Empty;
    public string SigningPublicKey { get; private set; } = string.Empty;
    public uint RecipientKeyVersion { get; private set; }
    public uint AccessEpoch { get; private set; }
    public string? Name { get; private set; }
    public string? Description { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string? IconKey { get; private set; }
    public string? IconColor { get; private set; }
    public AgentStatus Status { get; private set; }
    public Guid? LastUsedApiKeyId { get; private set; }
    public Instant? LastAccessAt { get; private set; }
    public string? LastIp { get; private set; }
    public string? LastHostname { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public Instant? EnrolledAt { get; private set; }
    public Guid? EnrolledBy { get; private set; }
    public Instant? DeactivatedAt { get; private set; }
    public Guid? DeactivatedBy { get; private set; }
    public Guid? DeactivationRequestId { get; private set; }
    public Instant? ReactivatedAt { get; private set; }
    public Guid? ReactivatedBy { get; private set; }

    public User? EnrolledByUser { get; private set; }
    public User? DeactivatedByUser { get; private set; }
    public User? ReactivatedByUser { get; private set; }

    private Agent() { }

    internal static Agent Create(
        Guid id,
        Guid organizationId,
        string publicKey,
        string signingPublicKey,
        string type,
        Instant now,
        string? name = null,
        Guid? apiKeyId = null)
    {
        var agent = new Agent
        {
            Id = id,
            OrganizationId = organizationId,
            PublicKey = publicKey,
            SigningPublicKey = signingPublicKey,
            RecipientKeyVersion = 1,
            Status = AgentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Name = name?.Trim(),
            Type = type.Trim(),
            LastUsedApiKeyId = apiKeyId,
        };

        agent.EmitUpserted();

        return agent;
    }

    internal void SetConnectionInfo(string? ip, string? hostname)
    {
        if (ip is not null)
        {
            LastIp = ip;
        }

        if (hostname is not null)
        {
            LastHostname = hostname;
        }
    }

    internal void UpdateOnConnect(Instant now, string? name, string? type)
    {
        if (name is not null)
        {
            Name = name.Trim();
        }

        if (type is not null)
        {
            Type = type.Trim();
        }

        UpdatedAt = now;
        EmitUpserted();
    }

    internal void RecordApiKeyUsage(Guid apiKeyId, Instant now, string? ip, string? hostname)
    {
        LastUsedApiKeyId = apiKeyId;
        LastAccessAt = now;
        SetConnectionInfo(ip, hostname);
    }

    internal void Activate(Guid enrolledBy, Instant now, string? name, string? type, string? iconKey, string? iconColor)
    {
        AccessEpoch = checked(AccessEpoch + 1);
        Status = AgentStatus.Active;
        EnrolledAt = now;
        EnrolledBy = enrolledBy;

        if (name is not null)
        {
            Name = name.Trim();
        }

        if (type is not null)
        {
            Type = type;
        }

        if (iconKey is not null)
        {
            IconKey = iconKey;
        }

        if (iconColor is not null)
        {
            IconColor = iconColor;
        }

        UpdatedAt = now;
        EmitUpserted();
    }

    internal void RequestDeactivation(Guid requestId, Guid deactivatedBy, Instant now)
    {
        if (Status == AgentStatus.Deactivating)
        {
            AddOrReplaceEvent(new AgentDeactivationRequestedEvent(
                DeactivationRequestId!.Value,
                Id,
                OrganizationId,
                DeactivatedBy!.Value,
                UpdatedAt));
            return;
        }

        if (Status == AgentStatus.Deactivated || requestId == Guid.Empty || deactivatedBy == Guid.Empty)
        {
            throw new InvalidOperationException("Agent cannot start deactivation in its current state.");
        }

        Status = AgentStatus.Deactivating;
        DeactivationRequestId = requestId;
        DeactivatedBy = deactivatedBy;
        UpdatedAt = now;
        AddEvent(new AgentDeactivationRequestedEvent(
            requestId, Id, OrganizationId, deactivatedBy, now));
    }

    internal void CompleteDeactivation(string deactivatedByName, Instant now)
    {
        if (Status != AgentStatus.Deactivating || DeactivatedBy is null)
        {
            throw new InvalidOperationException("Agent deactivation was not requested.");
        }

        Status = AgentStatus.Deactivated;
        DeactivatedAt = now;
        UpdatedAt = now;
        EmitDeactivated(deactivatedByName);
    }

    internal void Reactivate(Guid reactivatedBy, string reactivatedByName, Instant now)
    {
        AccessEpoch = checked(AccessEpoch + 1);
        Status = AgentStatus.Active;
        DeactivatedAt = null;
        DeactivatedBy = null;
        DeactivationRequestId = null;
        ReactivatedAt = now;
        ReactivatedBy = reactivatedBy;

        UpdatedAt = now;
        EmitReactivated(reactivatedByName);
    }

    internal void Delete(Guid deletedBy, string deletedByName, Instant now) => EmitDeleted(deletedBy, deletedByName, now);

    internal void Update(Instant now, string? name, string? description, string? type, string? iconKey, string? iconColor)
    {
        if (Status == AgentStatus.Deactivating)
        {
            throw new InvalidOperationException("Agent metadata cannot change while deactivation is in progress.");
        }

        if (name is not null)
        {
            Name = name;
        }

        if (description is not null)
        {
            Description = description;
        }

        if (type is not null)
        {
            Type = type;
        }

        if (iconKey is not null)
        {
            IconKey = iconKey;
        }

        if (iconColor is not null)
        {
            IconColor = iconColor;
        }

        UpdatedAt = now;
        EmitUpserted();
    }

    private void EmitUpserted() =>
        AddOrReplaceEvent(new AgentUpsertedEvent(
            Id,
            OrganizationId,
            Status,
            PublicKey,
            RecipientKeyVersion,
            SigningPublicKey,
            Name,
            Type,
            IconKey,
            IconColor,
            AccessEpoch,
            CurrentAccessEpochStartedAt(),
            UpdatedAt));

    private void EmitDeactivated(string deactivatedByName)
    {
        AddOrReplaceEvent(new AgentUpsertedEvent(
            Id,
            OrganizationId,
            Status,
            PublicKey,
            RecipientKeyVersion,
            SigningPublicKey,
            Name,
            Type,
            IconKey,
            IconColor,
            AccessEpoch,
            CurrentAccessEpochStartedAt(),
            UpdatedAt));
        AddEvent(new AgentDeactivatedEvent(
            Id,
            OrganizationId,
            DeactivatedBy!.Value,
            deactivatedByName,
            Name,
            AccessEpoch,
            UpdatedAt));
    }

    private void EmitReactivated(string reactivatedByName)
    {
        AddOrReplaceEvent(new AgentUpsertedEvent(
            Id,
            OrganizationId,
            Status,
            PublicKey,
            RecipientKeyVersion,
            SigningPublicKey,
            Name,
            Type,
            IconKey,
            IconColor,
            AccessEpoch,
            CurrentAccessEpochStartedAt(),
            UpdatedAt));
        AddEvent(new AgentReactivatedEvent(Id, OrganizationId, ReactivatedBy!.Value, reactivatedByName, Name, UpdatedAt));
    }

    private void EmitDeleted(Guid deletedBy, string deletedByName, Instant now) =>
        AddEvent(new AgentDeletedEvent(Id, OrganizationId, deletedBy, deletedByName, Name, now));

    private Instant? CurrentAccessEpochStartedAt() =>
        Status == AgentStatus.Active ? ReactivatedAt ?? EnrolledAt : null;
}
