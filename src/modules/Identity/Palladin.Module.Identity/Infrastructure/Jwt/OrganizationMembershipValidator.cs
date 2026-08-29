using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class OrganizationMembershipValidator(IdentityDbReadContext readContext)
    : IOrganizationMembershipValidator
{
    public Task<bool> IsCurrentAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        OrganizationOfflineAccessPolicy offlineAccessPolicy,
        uint offlineAccessPolicyVersion,
        CancellationToken ct = default) =>
        readContext.OrganizationMembers.AnyAsync(
            member => member.UserId == userId
                      && member.OrganizationId == organizationId
                      && member.AuthorizationVersion == authorizationVersion
                      && member.Organization.OfflineAccessPolicy == offlineAccessPolicy
                      && member.Organization.OfflineAccessPolicyVersion == offlineAccessPolicyVersion,
            ct);

    public Task<bool> IsActiveAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        CancellationToken ct = default) =>
        readContext.OrganizationMembers.AnyAsync(
            member => member.UserId == userId
                      && member.OrganizationId == organizationId
                      && member.AuthorizationVersion == authorizationVersion
                      && member.Status == OrganizationMemberStatus.Active,
            ct);
}
