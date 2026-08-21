using JetBrains.Annotations;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class DispatchVaultAccessReplicasJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Identity:DispatchVaultAccessReplicasJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "* * * * *";
    public int BatchSize { get; init; } = 100;
    public int RepairIntervalMinutes { get; init; } = 15;
}

[UsedImplicitly]
internal sealed class DispatchVaultAccessReplicasJob(
    IdentityDomainWriteContext domainWriteContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IOptions<DispatchVaultAccessReplicasJobOptions> options,
    IClock clock,
    ILogger<DispatchVaultAccessReplicasJob> logger) : ICronJob
{
    public string Name => "identity.dispatch-vault-access-replicas";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var publishers = eventPublishers.ToArray();
        if (publishers.Length == 0)
        {
            throw new InvalidOperationException("Vault-access replica dispatch requires an integration event publisher.");
        }

        var now = clock.GetCurrentInstant();
        var repairCutoff = now - Duration.FromMinutes(options.Value.RepairIntervalMinutes);
        var roles = await domainWriteContext.OrganizationRoleVaultAccessDispatches
            .Where(x => x.NextAttemptAt <= now
                        && (x.PublishedRevision < x.Revision
                            || x.PublishedRevision == x.Revision
                            && (x.PublishedAt == null || x.PublishedAt <= repairCutoff)))
            .OrderByDescending(x => x.PublishedRevision < x.Revision)
            .ThenBy(x => x.PublishedRevision < x.Revision
                ? x.NextAttemptAt
                : x.PublishedAt ?? x.NextAttemptAt)
            .ThenBy(x => x.OrganizationId)
            .ThenBy(x => x.RoleId)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken);
        var published = 0;
        var failed = 0;
        foreach (var dispatch in roles)
        {
            var revision = dispatch.Revision;
            try
            {
                IEvent message = dispatch.IsDeleted
                    ? new OrganizationRoleDeletedEvent(
                        dispatch.OrganizationId,
                        dispatch.RoleId,
                        revision,
                        dispatch.IsSystem,
                        dispatch.Permissions,
                        dispatch.OccurredAt)
                    : new OrganizationRoleUpsertedEvent(
                        dispatch.OrganizationId,
                        dispatch.RoleId,
                        revision,
                        dispatch.Change,
                        dispatch.IsSystem,
                        dispatch.Permissions,
                        dispatch.OccurredAt);
                await PublishAsync(publishers, message, cancellationToken);
                dispatch.MarkPublished(revision, clock.GetCurrentInstant());
                published++;
            }
            catch (Exception)
            {
                dispatch.MarkFailed(revision, clock.GetCurrentInstant());
                failed++;
            }

            await domainWriteContext.CommitAsync(cancellationToken);
        }

        domainWriteContext.Clear();

        now = clock.GetCurrentInstant();
        repairCutoff = now - Duration.FromMinutes(options.Value.RepairIntervalMinutes);
        var memberRoleSets = await domainWriteContext.OrganizationMemberRoleSetDispatches
            .Where(x => x.NextAttemptAt <= now
                        && (x.PublishedRevision < x.Revision
                            || x.PublishedRevision == x.Revision
                            && (x.PublishedAt == null || x.PublishedAt <= repairCutoff)))
            .OrderByDescending(x => x.PublishedRevision < x.Revision)
            .ThenBy(x => x.PublishedRevision < x.Revision
                ? x.NextAttemptAt
                : x.PublishedAt ?? x.NextAttemptAt)
            .ThenBy(x => x.OrganizationId)
            .ThenBy(x => x.UserId)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var dispatch in memberRoleSets)
        {
            var revision = dispatch.Revision;
            try
            {
                await PublishAsync(
                    publishers,
                    new OrganizationMemberRolesUpsertedEvent(
                        dispatch.OrganizationId,
                        dispatch.UserId,
                        dispatch.RoleIds,
                        revision,
                        dispatch.AuthorizationVersion,
                        dispatch.IsActive,
                        dispatch.UpdatedAt),
                    cancellationToken);
                dispatch.MarkPublished(revision, clock.GetCurrentInstant());
                published++;
            }
            catch (Exception)
            {
                dispatch.MarkFailed(revision, clock.GetCurrentInstant());
                failed++;
            }

            await domainWriteContext.CommitAsync(cancellationToken);
        }

        domainWriteContext.Clear();

        if (published > 0 || failed > 0)
        {
            logger.LogInformation(
                "Processed Vault-access replica dispatches: {PublishedCount} published, {FailedCount} pending retry",
                published,
                failed);
        }
    }

    private static async Task PublishAsync(
        IEnumerable<IEventPublisher> publishers,
        IEvent message,
        CancellationToken cancellationToken)
    {
        foreach (var publisher in publishers)
        {
            await publisher.PublishAsync(message, cancellationToken);
        }
    }
}
