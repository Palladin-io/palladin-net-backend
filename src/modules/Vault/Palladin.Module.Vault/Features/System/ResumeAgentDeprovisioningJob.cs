using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class ResumeAgentDeprovisioningJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:ResumeAgentDeprovisioningJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/5 * * * *";
    public int BatchSize { get; init; } = 100;
}

[UsedImplicitly]
internal sealed class ResumeAgentDeprovisioningJob(
    VaultDomainReadContext domainReadContext,
    IOptions<ResumeAgentDeprovisioningJobOptions> options,
    IPublishEndpoint publishEndpoint) : ICronJob
{
    public string Name => "vault.resume-agent-deprovisioning";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var operations = await domainReadContext.VaultPrincipalDeprovisionings
            .Where(x => x.PrincipalType == VaultPrincipalType.Agent
                        && x.Status != VaultPrincipalDeprovisioningStatus.Completed)
            .OrderBy(x => x.RequestedAt).ThenBy(x => x.Id)
            .Take(Math.Clamp(options.Value.BatchSize, 1, 200))
            .Select(x => new { x.OrganizationId, x.Id })
            .ToListAsync(cancellationToken);
        foreach (var operation in operations)
        {
            await publishEndpoint.Publish(
                new ResumeAgentDeprovisioningCommand(operation.OrganizationId, operation.Id), cancellationToken);
        }
    }
}
