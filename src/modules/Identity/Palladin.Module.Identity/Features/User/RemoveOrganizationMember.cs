using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record RemoveOrganizationMemberRequest
{
    public Guid UserId { get; init; }
}

[UsedImplicitly]
internal sealed class RemoveOrganizationMemberValidator : Validator<RemoveOrganizationMemberRequest>
{
    public RemoveOrganizationMemberValidator() => RuleFor(x => x.UserId).NotEmpty();
}

[PublicAPI]
internal sealed class RemoveOrganizationMemberEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<RemoveOrganizationMemberRequest>
{
    public override void Configure()
    {
        Delete("api/organization/members/{userId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        this.RequireEmailVerified();
        Tags("Identity/Organization");
        Summary(summary =>
        {
            summary.Summary = "Remove an organization member";
            summary.Description = "Starts staged removal of a non-owner Member. Membership remains effective until every affected Vault rotation commits.";
        });
    }

    public override async Task HandleAsync(RemoveOrganizationMemberRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var removedBy = User.GetUserId();
        if (organizationId is null || removedBy is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleAsync(x => x.Id == organizationId, ct);
        organization.FenceMembershipMutation();

        var actor = await domainWriteContext.OrganizationMembers
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(m => m.OrganizationId == organizationId && m.UserId == removedBy, ct);
        if (actor.Status != OrganizationMemberStatus.Active)
        {
            AddError(ErrorResponses.General("organization-membership-inactive"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var member = await domainWriteContext.OrganizationMembers
            .Include(m => m.User)
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == req.UserId, ct);
        if (member is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!actor.IsOwner && !OrganizationRoleAuthorization.IsSubsetOf(
                member.EffectivePermissions(), actor.EffectivePermissions()))
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        if (member.IsOwner)
        {
            AddError(ErrorResponses.General("organization-owner-protected"));
            await Send.ErrorsAsync(409, ct);
            return;
        }


        var now = clock.GetCurrentInstant();
        member.RequestRemoval(guidProvider.Generate(), removedBy.Value, now);
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
