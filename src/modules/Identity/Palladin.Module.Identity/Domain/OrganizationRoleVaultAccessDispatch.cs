using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;

namespace Palladin.Module.Identity.Domain;

internal sealed class OrganizationRoleVaultAccessDispatch
{
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public ulong Revision { get; private set; }
    public EntityChange Change { get; private set; }
    public bool IsDeleted { get; private set; }
    public bool IsSystem { get; private set; }
    public Permission Permissions { get; private set; }
    public Instant OccurredAt { get; private set; }
    public string DispatchKey { get; private set; } = string.Empty;
    public ulong PublishedRevision { get; private set; }
    public int PublishAttempts { get; private set; }
    public Instant NextAttemptAt { get; private set; }
    public Instant? LastAttemptAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Instant? PublishedAt { get; private set; }

    private OrganizationRoleVaultAccessDispatch() { }

    internal static OrganizationRoleVaultAccessDispatch Create(
        Guid organizationId,
        Guid roleId,
        ulong revision,
        EntityChange change,
        bool isDeleted,
        bool isSystem,
        Permission permissions,
        Instant occurredAt) =>
        new()
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            Revision = revision,
            Change = change,
            IsDeleted = isDeleted,
            IsSystem = isSystem,
            Permissions = permissions,
            OccurredAt = occurredAt,
            DispatchKey = Key(organizationId, roleId, revision),
            NextAttemptAt = occurredAt,
        };

    internal bool Apply(
        ulong revision,
        EntityChange change,
        bool isDeleted,
        bool isSystem,
        Permission permissions,
        Instant occurredAt)
    {
        if (revision < Revision || revision == Revision && IsDeleted && !isDeleted)
        {
            return false;
        }

        if (revision == Revision)
        {
            if (Change == change
                && IsDeleted == isDeleted
                && IsSystem == isSystem
                && Permissions == permissions
                && OccurredAt == occurredAt)
            {
                return false;
            }

            return false;
        }

        Revision = revision;
        Change = change;
        IsDeleted = isDeleted;
        IsSystem = isSystem;
        Permissions = permissions;
        OccurredAt = occurredAt;
        DispatchKey = Key(OrganizationId, RoleId, revision);
        PublishAttempts = 0;
        NextAttemptAt = occurredAt;
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

    private static string Key(Guid organizationId, Guid roleId, ulong revision) =>
        $"organization-role:{organizationId:N}:{roleId:N}:{revision}";

    private static Duration RetryDelay(int attempts) =>
        Duration.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
}
