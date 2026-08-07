using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class OrganizationMembershipValidator(IdentityDbReadContext readContext)
    : IOrganizationMembershipValidator
{
    public Task<bool> IsActiveAsync(Guid userId, Guid organizationId, CancellationToken ct = default) =>
        readContext.OrganizationMembers.AnyAsync(
            member => member.UserId == userId && member.OrganizationId == organizationId, ct);
}
