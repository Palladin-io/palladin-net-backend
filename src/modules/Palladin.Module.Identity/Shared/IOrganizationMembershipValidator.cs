using JetBrains.Annotations;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public interface IOrganizationMembershipValidator
{
    Task<bool> IsActiveAsync(Guid userId, Guid organizationId, CancellationToken ct = default);
}
