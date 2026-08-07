using FastEndpoints;

namespace Palladin.Module.Vault.Infrastructure.Authorization;

internal static class VaultMembershipEndpointExtensions
{
    public static void RequireVaultMembership<TRequest>(this Endpoint<TRequest> endpoint)
        where TRequest : notnull, IRequiresVaultMembership =>
        endpoint.PreProcessors(new RequireVaultMembershipPreProcessor<TRequest>());

    public static void RequireVaultMembership<TRequest, TResponse>(this Endpoint<TRequest, TResponse> endpoint)
        where TRequest : notnull, IRequiresVaultMembership =>
        endpoint.PreProcessors(new RequireVaultMembershipPreProcessor<TRequest>());
}
