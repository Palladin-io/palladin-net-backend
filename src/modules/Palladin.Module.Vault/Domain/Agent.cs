using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class Agent
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public AgentStatus Status { get; private set; }
    public string PublicKey { get; private set; } = string.Empty;
    public uint RecipientKeyVersion { get; private set; }
    public string SigningPublicKey { get; private set; } = string.Empty;
    public string? Name { get; private set; }
    public string? IconKey { get; private set; }
    public string? IconColor { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public uint AccessEpoch { get; private set; }
    public Instant? AccessEpochStartedAt { get; private set; }
    public uint LastProcessedDeactivationEpoch { get; private set; }
    public Instant? LastProcessedDeactivationAt { get; private set; }

    private Agent() { }

    internal static Agent Create(
        Guid id,
        Guid organizationId,
        AgentStatus status,
        string publicKey,
        uint recipientKeyVersion,
        string signingPublicKey,
        string? name,
        string? iconKey,
        string? iconColor,
        uint accessEpoch,
        Instant? accessEpochStartedAt,
        Instant updatedAt) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Status = status,
            PublicKey = publicKey,
            RecipientKeyVersion = recipientKeyVersion,
            SigningPublicKey = signingPublicKey,
            Name = name,
            IconKey = iconKey,
            IconColor = iconColor,
            UpdatedAt = updatedAt,
            AccessEpoch = accessEpoch,
            AccessEpochStartedAt = accessEpochStartedAt,
        };

    internal void Apply(
        AgentStatus status,
        string publicKey,
        uint recipientKeyVersion,
        string signingPublicKey,
        string? name,
        string? iconKey,
        string? iconColor,
        uint accessEpoch,
        Instant? accessEpochStartedAt,
        Instant updatedAt)
    {
        Status = status;
        PublicKey = publicKey;
        RecipientKeyVersion = recipientKeyVersion;
        SigningPublicKey = signingPublicKey;
        Name = name;
        IconKey = iconKey;
        IconColor = iconColor;
        UpdatedAt = updatedAt;
        AccessEpoch = accessEpoch;
        AccessEpochStartedAt = accessEpochStartedAt;
    }

    internal bool AcceptDeactivation(uint deactivatedAccessEpoch, Instant deactivatedAt)
    {
        if (deactivatedAccessEpoch < LastProcessedDeactivationEpoch
            || (deactivatedAccessEpoch == LastProcessedDeactivationEpoch
                && LastProcessedDeactivationAt is not null))
        {
            return false;
        }

        LastProcessedDeactivationEpoch = deactivatedAccessEpoch;
        LastProcessedDeactivationAt = deactivatedAt;
        if (deactivatedAccessEpoch >= AccessEpoch)
        {
            AccessEpoch = deactivatedAccessEpoch;
            Status = AgentStatus.Deactivated;
            if (deactivatedAt > UpdatedAt)
            {
                UpdatedAt = deactivatedAt;
            }

            AccessEpochStartedAt = null;
        }

        return true;
    }
}
