using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetOrgResponse(
    Guid OrgId,
    string Name,
    PlanType PlanType,
    int MemberCount,
    int SeatUsage,
    int SeatLimit);

[PublicAPI]
internal sealed class GetOrgEndpoint(IdentityDomainReadContext domainReadContext, IClock clock)
    : EndpointWithoutRequest<GetOrgResponse>
{
    public override void Configure()
    {
        Get("api/org");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Get current organization";
            summary.Description = "Returns the authenticated user's organization details including member count, reserved seat usage, and seat limit.";
        });
        Tags("Identity/Organization");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var org = await domainReadContext.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new GetOrgResponse(
                o.Id,
                o.Name,
                o.PlanType,
                o.Members.Count,
                o.Members.Count + o.Invitations.Count(invitation =>
                    invitation.AcceptedAt == null && invitation.ExpiresAt > now),
                o.SeatLimit))
            .FirstOrDefaultAsync(ct);

        if (org is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(org, ct);
    }
}
