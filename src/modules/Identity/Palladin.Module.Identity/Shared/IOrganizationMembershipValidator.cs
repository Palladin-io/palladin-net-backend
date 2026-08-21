using JetBrains.Annotations;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public interface IOrganizationMembershipValidator
{
    Task<bool> IsCurrentAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        CancellationToken ct = default);

    Task<bool> IsActiveAsync(
        Guid userId,
        Guid organizationId,
        uint authorizationVersion,
        CancellationToken ct = default);
}
