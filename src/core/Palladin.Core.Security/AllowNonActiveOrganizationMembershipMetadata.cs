using Microsoft.AspNetCore.Builder;

namespace Palladin.Core.Security;

public sealed class AllowNonActiveOrganizationMembershipMetadata
{
    public static AllowNonActiveOrganizationMembershipMetadata Instance { get; } = new();

    private AllowNonActiveOrganizationMembershipMetadata() { }
}

public static class OrganizationMembershipEndpointMetadataExtensions
{
    public static RouteHandlerBuilder AllowNonActiveOrganizationMembership(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(AllowNonActiveOrganizationMembershipMetadata.Instance);
}
