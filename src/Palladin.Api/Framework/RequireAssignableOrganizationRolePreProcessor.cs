using FastEndpoints;
using FluentValidation.Results;
using JetBrains.Annotations;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Shared;

namespace Palladin.Api.Framework;

[UsedImplicitly]
internal sealed class RequireAssignableOrganizationRolePreProcessor : IGlobalPreProcessor
{
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var user = context.HttpContext.User;
        var organizationId = user.GetOrganizationId();
        var userId = user.GetUserId();
        var roleValue = context.HttpContext.Request.RouteValues.GetValueOrDefault("roleId")?.ToString();
        if (organizationId is null || userId is null || !Guid.TryParse(roleValue, out var roleId))
        {
            return;
        }

        var validator = context.HttpContext.RequestServices
            .GetRequiredService<IOrganizationRoleAuthorizationValidator>();
        var snapshot = await validator.GetAssignableSnapshotAsync(
            organizationId.Value,
            roleId,
            userId.Value,
            ct);
        if (snapshot is null)
        {
            await context.HttpContext.Response.SendErrorsAsync(
                [new ValidationFailure(
                    string.Empty,
                    ErrorResponses.General("organization-role-assignment-forbidden"))],
                StatusCodes.Status403Forbidden,
                cancellation: ct);
            return;
        }

        context.HttpContext.Items[RequireAssignableOrganizationRoleAttribute.AuthorizedRevisionItemName] =
            snapshot.Revision;
        context.HttpContext.Items[RequireAssignableOrganizationRoleAttribute.ActorPermissionsItemName] =
            snapshot.ActorPermissions;
        context.HttpContext.Items[RequireAssignableOrganizationRoleAttribute.ActorIsOwnerItemName] =
            snapshot.ActorIsOwner;
    }
}
