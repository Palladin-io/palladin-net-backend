using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Guid;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CreateOrganizationRoleRequest
{
    public string Name { get; init; } = string.Empty;
    public int Permissions { get; init; }
}

[UsedImplicitly]
internal sealed class CreateOrganizationRoleValidator : Validator<CreateOrganizationRoleRequest>
{
    public CreateOrganizationRoleValidator()
    {
        RuleFor(x => x.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .MaximumLength(100);
        RuleFor(x => x.Permissions)
            .Must(value => OrganizationRolePermissions.IsAssignable((Permission)value));
    }
}

[PublicAPI]
internal sealed class CreateOrganizationRoleEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<CreateOrganizationRoleRequest, OrganizationRoleItem>
{
    public override void Configure()
    {
        Post("api/organization/roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Create a custom organization role";
            summary.Description = "Creates a tenant-scoped role from the supported assignable permission set.";
        });
    }

    public override async Task HandleAsync(CreateOrganizationRoleRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(x => x.Id == organizationId, ct);
        organization.FenceMembershipMutation();

        var actor = await domainWriteContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organizationId && member.UserId == userId, ct);
        var requestedPermissions = (Permission)req.Permissions;
        if (!OrganizationRoleAuthorization.CanManageCustomRole(
                actor, Permission.None, requestedPermissions))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var normalizedName = Role.NormalizeName(req.Name);
        if (await domainWriteContext.Roles.AnyAsync(
                role => role.OrganizationId == organizationId && role.NormalizedName == normalizedName, ct))
        {
            AddError(ErrorResponses.General("organization-role-name-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var role = Role.Create(
            guidProvider.Generate(),
            organizationId.Value,
            req.Name,
            requestedPermissions,
            isSystem: false,
            clock.GetCurrentInstant());
        domainWriteContext.Add(role);

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            AddError(ErrorResponses.General("organization-role-name-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.ResponseAsync(
            new OrganizationRoleItem(
                role.Id, role.Name, (int)role.Permissions, role.IsSystem, CanAssign: true),
            201,
            ct);
    }
}
