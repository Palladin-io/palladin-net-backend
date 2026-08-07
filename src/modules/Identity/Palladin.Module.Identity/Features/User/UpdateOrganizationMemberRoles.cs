using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrganizationMemberRolesRequest
{
    public Guid UserId { get; init; }
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}

[UsedImplicitly]
internal sealed class UpdateOrganizationMemberRolesValidator : Validator<UpdateOrganizationMemberRolesRequest>
{
    public UpdateOrganizationMemberRolesValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleIds)
            .NotEmpty()
            .Must(roleIds => roleIds.Distinct().Count() == roleIds.Count);
    }
}

[PublicAPI]
internal sealed class UpdateOrganizationMemberRolesEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateOrganizationMemberRolesRequest>
{
    public override void Configure()
    {
        Put("api/organization/members/{userId}/roles");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Replace an organization member's roles";
            summary.Description = "Assigns a validated set of organization roles to a non-owner member.";
        });
    }

    public override async Task HandleAsync(UpdateOrganizationMemberRolesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var changedBy = User.GetUserId();
        if (organizationId is null || changedBy is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var member = await domainWriteContext.OrganizationMembers
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == req.UserId, ct);
        if (member is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (member.IsOwner)
        {
            AddError(ErrorResponses.General("organization-owner-protected"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var requestedRoleIds = req.RoleIds.ToHashSet();
        var roles = await domainWriteContext.Roles
            .Where(role => role.OrganizationId == organizationId && requestedRoleIds.Contains(role.Id))
            .ToListAsync(ct);
        if (roles.Count != requestedRoleIds.Count)
        {
            AddError(ErrorResponses.General("organization-role-invalid"));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        member.ReplaceRoles(
            roles, member.User.DisplayName, changedBy.Value, User.GetDisplayName(), clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
