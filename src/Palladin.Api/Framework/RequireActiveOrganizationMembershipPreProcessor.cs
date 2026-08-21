using FastEndpoints;
using FluentValidation.Results;
using JetBrains.Annotations;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Shared;

namespace Palladin.Api.Framework;

[UsedImplicitly]
internal sealed class RequireActiveOrganizationMembershipPreProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
        {
            return;
        }

        await ActiveOrganizationMembershipGate.EnforceAsync(context, ct);
    }
}

[UsedImplicitly]
internal sealed class RequireActiveOrganizationMembershipForAllVerbsPreProcessor : IGlobalPreProcessor
{
    public Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct) =>
        ActiveOrganizationMembershipGate.EnforceAsync(context, ct);
}

internal static class ActiveOrganizationMembershipGate
{
    internal static async Task EnforceAsync(IPreProcessorContext context, CancellationToken ct)
    {

        var user = context.HttpContext.User;
        var userId = user.GetUserId();
        var organizationId = user.GetOrganizationId();
        var authorizationVersion = user.GetAuthorizationVersion();

        // Only a complete user-JWT identity is in scope. Anonymous, Agent and system principals have
        // separate authentication boundaries and don't carry this claim set.
        if (userId is null || organizationId is null || authorizationVersion is null)
        {
            return;
        }

        var validator = context.HttpContext.RequestServices
            .GetRequiredService<IOrganizationMembershipValidator>();
        if (await validator.IsActiveAsync(
                userId.Value,
                organizationId.Value,
                authorizationVersion.Value,
                ct))
        {
            return;
        }

        await context.HttpContext.Response.SendErrorsAsync(
            [new ValidationFailure(string.Empty, ErrorResponses.General("organization-membership-inactive"))],
            StatusCodes.Status403Forbidden,
            cancellation: ct);
    }
}
