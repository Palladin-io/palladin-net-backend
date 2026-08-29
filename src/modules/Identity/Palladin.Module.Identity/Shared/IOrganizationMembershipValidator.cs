using JetBrains.Annotations;
using Palladin.Module.Identity.Contracts.ValueObjects;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public interface IOrganizationMembershipValidator
{
    Task<bool> IsCurrentAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        OrganizationOfflineAccessPolicy offlineAccessPolicy,
        uint offlineAccessPolicyVersion,
        CancellationToken ct = default);

    Task<bool> IsActiveAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        CancellationToken ct = default);
}
