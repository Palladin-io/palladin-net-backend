using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class RoleVaultAccessPolicyDispatch
{
    public Guid OrganizationId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid OperationId { get; private set; }
    public ulong Revision { get; private set; }
    public Guid ChangedBy { get; private set; }
    public Guid[] SelectedVaultIds { get; private set; } = [];
    public Instant OccurredAt { get; private set; }
    public string DispatchKey { get; private set; } = string.Empty;
    public ulong PublishedRevision { get; private set; }
    public int PublishAttempts { get; private set; }
    public Instant NextAttemptAt { get; private set; }
    public Instant? LastAttemptAt { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Instant? PublishedAt { get; private set; }

    private RoleVaultAccessPolicyDispatch() { }

    internal static RoleVaultAccessPolicyDispatch Create(
        Guid organizationId,
        Guid roleId,
        Guid operationId,
        ulong revision,
        Guid changedBy,
        IReadOnlyCollection<Guid> selectedVaultIds,
        Instant occurredAt) =>
        new()
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            OperationId = operationId,
            Revision = revision,
            ChangedBy = changedBy,
            SelectedVaultIds = Normalize(selectedVaultIds),
            OccurredAt = occurredAt,
            DispatchKey = Key(organizationId, roleId, revision),
            NextAttemptAt = occurredAt,
        };

    internal bool Apply(
        Guid operationId,
        ulong revision,
        Guid changedBy,
        IReadOnlyCollection<Guid> selectedVaultIds,
        Instant occurredAt)
    {
        var normalizedVaultIds = Normalize(selectedVaultIds);
        if (revision < Revision)
        {
            return false;
        }

        if (revision == Revision)
        {
            if (OperationId == operationId
                && ChangedBy == changedBy
                && SelectedVaultIds.SequenceEqual(normalizedVaultIds)
                && OccurredAt == occurredAt)
            {
                return false;
            }

            return false;
        }

        OperationId = operationId;
        Revision = revision;
        ChangedBy = changedBy;
        SelectedVaultIds = normalizedVaultIds;
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
        if (revision != Revision || PublishedRevision >= revision)
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

    private static Guid[] Normalize(IEnumerable<Guid> vaultIds) =>
        vaultIds.Distinct().Order().ToArray();

    private static string Key(Guid organizationId, Guid roleId, ulong revision) =>
        $"role-vault-access-policy:{organizationId:N}:{roleId:N}:{revision}";

    private static Duration RetryDelay(int attempts) =>
        Duration.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
}
