namespace Palladin.Module.Identity.Contracts.ValueObjects;

public enum OrganizationOfflineAccessPolicy : ushort
{
    Disabled = 0,
    OneHour = 1,
    FourHours = 2,
    TwentyFourHours = 3,
}

public sealed record OrganizationOfflineAccessAuthority(
    uint OrganizationMembershipGeneration,
    OrganizationOfflineAccessPolicy OfflineAccessPolicy,
    uint OfflineAccessPolicyVersion);

public interface IOrganizationOfflineAccessAuthority
{
    Task<OrganizationOfflineAccessAuthority?> GetCurrentAsync(
        Guid principalId,
        Guid organizationId,
        uint organizationMembershipGeneration,
        CancellationToken cancellationToken = default);
}
