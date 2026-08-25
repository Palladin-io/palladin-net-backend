using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record OrganizationMemberDirectoryItem(Guid UserId, string DisplayName);

[PublicAPI]
public sealed record ListOrganizationMemberDirectoryResponse(
    IReadOnlyList<OrganizationMemberDirectoryItem> Items);

[PublicAPI]
internal sealed class ListOrganizationMemberDirectoryEndpoint(
    IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<ListOrganizationMemberDirectoryResponse>
{
    public override void Configure()
    {
        Get("api/organization/member-directory");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "List the organization member directory";
            summary.Description =
                "Returns minimal display identities for current and former members of the active organization.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        if (organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var items = await domainReadContext.OrganizationMemberDirectoryEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.DisplayName)
            .ThenBy(entry => entry.UserId)
            .Select(entry => new OrganizationMemberDirectoryItem(entry.UserId, entry.DisplayName))
            .ToListAsync(ct);

        await Send.OkAsync(new ListOrganizationMemberDirectoryResponse(items), ct);
    }
}
