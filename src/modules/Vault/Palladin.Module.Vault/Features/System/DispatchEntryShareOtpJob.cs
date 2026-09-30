using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class DispatchEntryShareOtpJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Vault:DispatchEntryShareOtpJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "* * * * *";
    public int BatchSize { get; init; } = 100;
    public int MaximumBatches { get; init; } = 10;
}

[UsedImplicitly]
internal sealed class DispatchEntryShareOtpJob(
    VaultDomainWriteContext context, EntryShareOtpDispatcher dispatcher, IOptions<DispatchEntryShareOtpJobOptions> options)
    : ICronJob
{
    public string Name => "vault.dispatch-entry-share-otp";
    public string Expression => options.Value.Expression;
    public bool Enabled => options.Value.Enabled;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        for (var batch = 0; batch < Math.Clamp(options.Value.MaximumBatches, 1, 100); batch++)
        {
            var pending = await context.EntryShareSessions.Where(x => x.ProtectedOtp != null)
                .OrderBy(x => x.OtpExpiresAt).ThenBy(x => x.ShareId).ThenBy(x => x.Id)
                .Take(Math.Clamp(options.Value.BatchSize, 1, 200))
                .Select(x => new { x.ShareId, x.Id, x.OtpGeneration }).ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            foreach (var item in pending)
            {
                await dispatcher.DispatchAsync(item.ShareId, item.Id, item.OtpGeneration, cancellationToken);
                context.Clear();
            }
        }
    }
}
