using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class OrganizationOfflineAccessAuthorityReader(
    IdentityDomainReadContext readContext) : IOrganizationOfflineAccessAuthority
{
    public Task<OrganizationOfflineAccessAuthority?> GetCurrentAsync(
        Guid principalId,
        Guid organizationId,
        uint organizationMembershipGeneration,
        CancellationToken cancellationToken = default) =>
        (from member in readContext.OrganizationMembers
         join organization in readContext.Organizations on member.OrganizationId equals organization.Id
         where member.UserId == principalId
               && member.OrganizationId == organizationId
               && member.AuthorizationVersion == organizationMembershipGeneration
               && member.Status == OrganizationMemberStatus.Active
         select new OrganizationOfflineAccessAuthority(
             member.AuthorizationVersion,
             organization.OfflineAccessPolicy,
             organization.OfflineAccessPolicyVersion))
        .SingleOrDefaultAsync(cancellationToken);
}
