using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Palladin.Core.Hangfire.CronJobs;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[UsedImplicitly]
internal sealed class AnnounceOrganizationApproversJobOptions : ICronJobOptions
{
    public const string Position = "Modules:Identity:AnnounceOrganizationApproversJob";
    public bool Enabled { get; init; } = true;
    public string Expression { get; init; } = "23 * * * *";
    public int BatchSize { get; init; } = 500;
}

// Idempotent anti-entropy for Vault's approver replica: re-announces every membership's current approver
// eligibility in keyset pages. It backfills organizations created before the command existed and heals a
// lost message; Vault ignores an announcement that does not advance the membership's UpdatedAt.
[UsedImplicitly]
internal sealed class AnnounceOrganizationApproversJob(
    IdentityDomainWriteContext context,
    IOptions<AnnounceOrganizationApproversJobOptions> options) : ICronJob
{
    public string Name => "identity.announce-organization-approvers";
    public bool Enabled => options.Value.Enabled;
    public string Expression => options.Value.Expression;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = options.Value.BatchSize;
        Guid? afterOrganizationId = null;
        Guid? afterUserId = null;
        while (true)
        {
            var query = context.OrganizationMembers
                .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
                .AsQueryable();
            if (afterOrganizationId is { } organizationId && afterUserId is { } userId)
            {
                query = query.Where(member => member.OrganizationId.CompareTo(organizationId) > 0
                    || (member.OrganizationId == organizationId && member.UserId.CompareTo(userId) > 0));
            }

            var page = await query
                .OrderBy(member => member.OrganizationId)
                .ThenBy(member => member.UserId)
                .Take(batchSize)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);
            foreach (var member in page)
            {
                member.AnnounceApproverEligibility();
            }

            await context.CommitAsync(cancellationToken);
            context.Clear();
            if (page.Count < batchSize)
            {
                return;
            }

            afterOrganizationId = page[^1].OrganizationId;
            afterUserId = page[^1].UserId;
        }
    }
}
