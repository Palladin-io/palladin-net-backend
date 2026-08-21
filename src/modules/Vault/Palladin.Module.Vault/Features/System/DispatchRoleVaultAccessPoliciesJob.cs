using JetBrains.Annotations;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class DispatchRoleVaultAccessPoliciesJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:DispatchRoleVaultAccessPoliciesJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "* * * * *";
    public int BatchSize { get; init; } = 100;
}

[UsedImplicitly]
internal sealed class DispatchRoleVaultAccessPoliciesJob(
    VaultDomainWriteContext domainWriteContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IOptions<DispatchRoleVaultAccessPoliciesJobOptions> options,
    IClock clock,
    ILogger<DispatchRoleVaultAccessPoliciesJob> logger) : ICronJob
{
    public string Name => "vault.dispatch-role-vault-access-policies";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    [DisableConcurrentExecution(300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var publishers = eventPublishers.ToArray();
        if (publishers.Length == 0)
        {
            throw new InvalidOperationException("Role Vault-access policy dispatch requires an integration event publisher.");
        }

        var now = clock.GetCurrentInstant();
        var dispatches = await domainWriteContext.RoleVaultAccessPolicyDispatches
            .Where(x => x.PublishedRevision < x.Revision && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .ThenBy(x => x.OrganizationId)
            .ThenBy(x => x.RoleId)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken);
        var published = 0;
        var failed = 0;
        foreach (var dispatch in dispatches)
        {
            var revision = dispatch.Revision;
            try
            {
                var message = new RoleVaultAccessPolicyChangedEvent(
                    dispatch.OrganizationId,
                    dispatch.RoleId,
                    dispatch.OperationId,
                    revision,
                    dispatch.ChangedBy,
                    dispatch.SelectedVaultIds,
                    dispatch.OccurredAt);
                foreach (var publisher in publishers)
                {
                    await publisher.PublishAsync(message, cancellationToken);
                }

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
                "Processed role Vault-access policy dispatches: {PublishedCount} published, {FailedCount} pending retry",
                published,
                failed);
        }
    }
}
