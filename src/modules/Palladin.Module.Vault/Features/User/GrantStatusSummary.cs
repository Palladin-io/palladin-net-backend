using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GrantStatusSummaryResponse(
    int Pending,
    int Active,
    int Expired,
    int Revoked,
    int Consumed,
    int Denied);

[PublicAPI]
internal sealed class GrantStatusSummaryEndpoint(VaultDomainReadContext domainReadContext)
    : EndpointWithoutRequest<GrantStatusSummaryResponse>
{
    public override void Configure()
    {
        Get("api/grants/summary");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Grant status summary (counts per status)";
            summary.Description = "Returns the count of grants in each status for the caller's organization. All six keys are always present; statuses with no grants return 0. One GROUP BY query — no pagination. Same authorization as GET /api/grants.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;

        var counts = await domainReadContext.Grants
            .Where(g => g.OrganizationId == organizationId)
            .GroupBy(g => g.Status)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToListAsync(ct);

        var byStatus = counts.ToDictionary(x => x.Key, x => x.Count);

        await Send.OkAsync(new GrantStatusSummaryResponse(
            Pending: byStatus.GetValueOrDefault(GrantStatus.Pending),
            Active: byStatus.GetValueOrDefault(GrantStatus.Active),
            Expired: byStatus.GetValueOrDefault(GrantStatus.Expired),
            Revoked: byStatus.GetValueOrDefault(GrantStatus.Revoked),
            Consumed: byStatus.GetValueOrDefault(GrantStatus.Consumed),
            Denied: byStatus.GetValueOrDefault(GrantStatus.Denied)), ct);
    }
}
