using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Module.Agents.Features;

[UsedImplicitly]
internal sealed class CleanupAgentPairingsJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Agents:CleanupAgentPairingsJob";

    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "*/1 * * * *";
    public int TerminalRetentionMinutes { get; init; } = 15;
    public int BatchSize { get; init; } = 500;
}

/// <summary>
/// Releases expired name reservations and removes terminal pairing material in bounded batches.
/// Approved encrypted credential envelopes remain available for the configured retrieval grace.
/// </summary>
[UsedImplicitly]
internal sealed class CleanupAgentPairingsJob(
    AgentsDomainWriteContext domainWriteContext,
    IOptions<CleanupAgentPairingsJobOptions> options,
    IClock clock) : ICronJob
{
    public string Name => "agents.cleanup-agent-pairings";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        await ExpirePendingAsync(now, cancellationToken);
        await DeleteTerminalAsync(
            now - Duration.FromMinutes(options.Value.TerminalRetentionMinutes),
            cancellationToken);
    }

    private async Task ExpirePendingAsync(Instant now, CancellationToken ct)
    {
        while (true)
        {
            var expired = await domainWriteContext.AgentPairingRequests
                .Where(x => x.Status == AgentPairingStatus.Pending && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt)
                .ThenBy(x => x.Id)
                .Take(options.Value.BatchSize)
                .ToListAsync(ct);
            if (expired.Count == 0)
            {
                return;
            }

            foreach (var pairing in expired)
            {
                pairing.TryExpire(now);
            }

            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A request was claimed or resolved after selection. Re-read the authoritative state.
            }
            finally
            {
                domainWriteContext.Clear();
            }

            if (expired.Count < options.Value.BatchSize)
            {
                return;
            }
        }
    }

    private async Task DeleteTerminalAsync(Instant cutoff, CancellationToken ct)
    {
        while (true)
        {
            var terminal = await domainWriteContext.AgentPairingRequests
                .Where(x => x.Status != AgentPairingStatus.Pending && x.UpdatedAt < cutoff)
                .OrderBy(x => x.UpdatedAt)
                .ThenBy(x => x.Id)
                .Take(options.Value.BatchSize)
                .ToListAsync(ct);
            if (terminal.Count == 0)
            {
                return;
            }

            domainWriteContext.RemoveRange(terminal);
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A terminal row changed after selection. Drop the stale batch and re-evaluate it.
            }
            finally
            {
                domainWriteContext.Clear();
            }

            if (terminal.Count < options.Value.BatchSize)
            {
                return;
            }
        }
    }
}
