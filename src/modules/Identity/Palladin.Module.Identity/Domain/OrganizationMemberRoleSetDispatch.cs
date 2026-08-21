using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationMemberRoleSetDispatch
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid[] RoleIds { get; private set; } = [];
    public ulong Revision { get; private set; }
    public uint AuthorizationVersion { get; private set; }
    public bool IsActive { get; private set; }
    public Instant UpdatedAt { get; private set; }
    public string DispatchKey { get; private set; } = string.Empty;
    public ulong PublishedRevision { get; private set; }
    public int PublishAttempts { get; private set; }
    public Instant NextAttemptAt { get; private set; }
    public Instant? LastAttemptAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Instant? PublishedAt { get; private set; }

    private OrganizationMemberRoleSetDispatch() { }

    internal static OrganizationMemberRoleSetDispatch Create(
        Guid organizationId,
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        ulong revision,
        uint authorizationVersion,
        bool isActive,
        Instant updatedAt) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            RoleIds = Normalize(roleIds),
            Revision = revision,
            AuthorizationVersion = authorizationVersion,
            IsActive = isActive,
            UpdatedAt = updatedAt,
            DispatchKey = Key(organizationId, userId, revision),
            NextAttemptAt = updatedAt,
        };

    internal bool Apply(
        IReadOnlyCollection<Guid> roleIds,
        ulong revision,
        uint authorizationVersion,
        bool isActive,
        Instant updatedAt)
    {
        var normalizedRoleIds = Normalize(roleIds);
        if (revision < Revision || revision == Revision && !IsActive && isActive)
        {
            return false;
        }

        if (revision == Revision)
        {
            if (RoleIds.SequenceEqual(normalizedRoleIds)
                && AuthorizationVersion == authorizationVersion
                && IsActive == isActive
                && UpdatedAt == updatedAt)
            {
                return false;
            }

            return false;
        }

        RoleIds = normalizedRoleIds;
        Revision = revision;
        AuthorizationVersion = authorizationVersion;
        IsActive = isActive;
        UpdatedAt = updatedAt;
        DispatchKey = Key(OrganizationId, UserId, revision);
        PublishAttempts = 0;
        NextAttemptAt = updatedAt;
        LastAttemptAt = null;
        LastErrorCode = null;
        return true;
    }

    internal void MarkPublished(ulong revision, Instant now)
    {
        if (revision != Revision || PublishedRevision > revision)
        {
            return;
        }

        PublishedRevision = revision;
        LastAttemptAt = now;
        LastErrorCode = null;
        PublishedAt = now;
    }

    internal void MarkFailed(ulong revision, Instant now)
    {
        if (revision != Revision)
        {
            return;
        }

        PublishAttempts = PublishAttempts == int.MaxValue ? int.MaxValue : PublishAttempts + 1;
        LastAttemptAt = now;
        LastErrorCode = "broker-publish-failed";
        NextAttemptAt = now + RetryDelay(PublishAttempts);
    }

    private static Guid[] Normalize(IEnumerable<Guid> roleIds) =>
        roleIds.Distinct().Order().ToArray();

    private static string Key(Guid organizationId, Guid userId, ulong revision) =>
        $"organization-member-role-set:{organizationId:N}:{userId:N}:{revision}";

    private static Duration RetryDelay(int attempts) =>
        Duration.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
}
