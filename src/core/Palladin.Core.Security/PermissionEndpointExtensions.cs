using FastEndpoints;

namespace Palladin.Core.Security;

public static class PermissionEndpointExtensions
{
    public static void RequirePermission<TRequest>(this Endpoint<TRequest> endpoint, Permission permission)
        where TRequest : notnull =>
        endpoint.PreProcessors(new RequirePermissionPreProcessor<TRequest>(permission));

    public static void RequirePermission<TRequest, TResponse>(this Endpoint<TRequest, TResponse> endpoint, Permission permission)
        where TRequest : notnull =>
        endpoint.PreProcessors(new RequirePermissionPreProcessor<TRequest>(permission));

    public static void RequireEmailVerified<TRequest>(this Endpoint<TRequest> endpoint)
        where TRequest : notnull =>
        endpoint.PreProcessors(new RequireEmailVerifiedPreProcessor<TRequest>());

    public static void RequireEmailVerified<TRequest, TResponse>(this Endpoint<TRequest, TResponse> endpoint)
        where TRequest : notnull =>
        endpoint.PreProcessors(new RequireEmailVerifiedPreProcessor<TRequest>());
}
