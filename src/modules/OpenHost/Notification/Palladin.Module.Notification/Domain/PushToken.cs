using Palladin.Core.Types;
using NodaTime;

namespace Palladin.Module.Notification.Domain;

internal sealed class PushToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public PushPlatform Platform { get; private set; }
    public string? DeviceName { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private PushToken() { }

    internal static PushToken Create(
        Guid id,
        Guid userId,
        Guid organizationId,
        string token,
        PushPlatform platform,
        string? deviceName,
        Instant now) =>
        new()
        {
            Id = id,
            UserId = userId,
            OrganizationId = organizationId,
            Token = token,
            Platform = platform,
            DeviceName = deviceName,
            CreatedAt = now,
            UpdatedAt = now,
        };

    // Re-registration of an existing device token. A token is per-installation, so the same value may
    // be re-submitted by a different user/org after a device hand-off — reassign ownership to the
    // current registrant so push never reaches a previous user.
    internal void Reassign(Guid userId, Guid organizationId, PushPlatform platform, string? deviceName, Instant now)
    {
        UserId = userId;
        OrganizationId = organizationId;
        Platform = platform;
        DeviceName = deviceName;
        UpdatedAt = now;
    }
}
