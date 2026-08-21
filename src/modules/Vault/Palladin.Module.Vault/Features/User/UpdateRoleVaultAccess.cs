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
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record UpdateRoleVaultAccessRequest
{
    public Guid RoleId { get; init; }
    public IReadOnlyCollection<Guid> VaultIds { get; init; } = [];
}

[PublicAPI]
public sealed record UpdateRoleVaultAccessResponse(
    Guid? OperationId,
    string Status,
    RoleVaultAccessImpact Impact);

[UsedImplicitly]
internal sealed class UpdateRoleVaultAccessValidator : Validator<UpdateRoleVaultAccessRequest>
{
    public UpdateRoleVaultAccessValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.VaultIds).NotNull().Must(vaultIds => vaultIds.Count <= 100);
        RuleForEach(x => x.VaultIds).NotEmpty();
    }
}

[PublicAPI]
[RequireAssignableOrganizationRole]
internal sealed class UpdateRoleVaultAccessEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<UpdateRoleVaultAccessRequest, UpdateRoleVaultAccessResponse>
{
    public override void Configure()
    {
        Put("api/organization/roles/{roleId}/vault-access");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement | Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Role access");
        Summary(summary =>
        {
            summary.Summary = "Replace desired Vault access for an organization role";
            summary.Description = "Persists desired state only and returns pending client work. It never creates or removes structural Vault membership or key envelopes.";
        });
    }

    public override async Task HandleAsync(UpdateRoleVaultAccessRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (HttpContext.Items[RequireAssignableOrganizationRoleAttribute.AuthorizedRevisionItemName]
                is not ulong authorizedRoleRevision
            || HttpContext.Items[RequireAssignableOrganizationRoleAttribute.ActorPermissionsItemName]
                is not Permission authorizedByPermissions
            || HttpContext.Items[RequireAssignableOrganizationRoleAttribute.ActorIsOwnerItemName]
                is not bool actorIsOwner)
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var role = await domainWriteContext.OrganizationRoleDirectory.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.RoleId == req.RoleId,
            ct);
        if (role is null || role.IsDeleted)
        {
            AddError(ErrorResponses.General("role-vault-access-role-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (role.SourceRevision != authorizedRoleRevision)
        {
            AddError(ErrorResponses.General("role-vault-access-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (!actorIsOwner && (role.Permissions & authorizedByPermissions) != role.Permissions)
        {
            AddError(ErrorResponses.General("organization-role-assignment-forbidden"));
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var requestedVaultIds = req.VaultIds.Distinct().Order().ToArray();
        var policySet = await domainWriteContext.RoleVaultAccessPolicySets
            .Include(x => x.Policies)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.RoleId == req.RoleId, ct);
        if (policySet is not null && policySet.AuthorizedRoleRevision != authorizedRoleRevision)
        {
            AddError(ErrorResponses.General("role-vault-access-conflict"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var currentVaultIds = policySet?.Policies.Select(x => x.VaultId).Order().ToArray() ?? [];
        var authorizationVaultIds = currentVaultIds.Concat(requestedVaultIds).Distinct().Order().ToArray();
        var authorizedVaults = await domainWriteContext.Vaults
            .Include(x => x.VaultMembers)
            .Where(x => x.OrganizationId == organizationId
                        && authorizationVaultIds.Contains(x.Id))
            .ToListAsync(ct);
        if (authorizedVaults.Count != authorizationVaultIds.Length
            || authorizedVaults.Any(x => x.VaultMembers.All(member => member.UserId != userId)))
        {
            AddError(ErrorResponses.General("role-vault-access-selection-invalid"));
            await Send.ErrorsAsync(404, ct);
            return;
        }

        if (authorizedVaults.Any(x => requestedVaultIds.Contains(x.Id) && x.IsDefault))
        {
            AddError(ErrorResponses.General("default-vault-not-shareable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (policySet is null && requestedVaultIds.Length == 0)
        {
            await Send.OkAsync(new UpdateRoleVaultAccessResponse(
                null,
                RoleVaultAccessStatus.Unchanged,
                new RoleVaultAccessImpact(0, 0, 0)), ct);
            return;
        }

        var change = policySet?.Preview(requestedVaultIds)
                     ?? new RoleVaultAccessPolicyChange(requestedVaultIds, []);
        if (!change.HasChanges && policySet?.LastOperationId is { } unchangedOperationId)
        {
            var unchangedOperation = await domainWriteContext.RoleVaultAccessOperations.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == unchangedOperationId, ct);
            await Send.ResponseAsync(ToResponse(unchangedOperation), 202, ct);
            return;
        }

        role.FencePolicyMutation();
        foreach (var vault in authorizedVaults)
        {
            vault.FenceRolePolicyMutation();
        }

        var activeRoleMemberIds = await domainWriteContext.OrganizationMemberRoleSets
            .Where(x => x.OrganizationId == organizationId
                        && x.IsActive
                        && x.RoleIds.Contains(req.RoleId))
            .Select(x => x.UserId)
            .ToArrayAsync(ct);
        var affectedVaultIds = change.AddedVaultIds.Concat(change.RemovedVaultIds).Distinct().ToArray();
        var existingMemberships = await domainWriteContext.VaultMembers
            .Where(x => x.OrganizationId == organizationId
                        && activeRoleMemberIds.Contains(x.UserId)
                        && affectedVaultIds.Contains(x.VaultId))
            .Select(x => new { x.UserId, x.VaultId })
            .ToListAsync(ct);
        var existingMembershipSet = existingMemberships
            .Select(x => (x.UserId, x.VaultId))
            .ToHashSet();
        var membersWithAdds = activeRoleMemberIds
            .Where(memberId => change.AddedVaultIds.Any(vaultId =>
                !existingMembershipSet.Contains((memberId, vaultId))))
            .ToHashSet();
        var membersWithRemovals = activeRoleMemberIds
            .Where(memberId => change.RemovedVaultIds.Any(vaultId =>
                existingMembershipSet.Contains((memberId, vaultId))))
            .ToHashSet();
        var impact = new RoleVaultAccessImpact(
            membersWithAdds.Count,
            membersWithRemovals.Count,
            activeRoleMemberIds.Distinct().Count(memberId =>
                !membersWithAdds.Contains(memberId) && !membersWithRemovals.Contains(memberId)));

        var now = clock.GetCurrentInstant();
        if (policySet?.LastOperationId is { } previousOperationId)
        {
            var previousOperation = await domainWriteContext.RoleVaultAccessOperations.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == previousOperationId, ct);
            previousOperation.Supersede(now);
        }

        var operation = RoleVaultAccessOperation.Create(
            organizationId.Value,
            guidProvider.Generate(),
            req.RoleId,
            impact.AddsAwaitingProvisioning,
            impact.RemovalsAwaitingSourceReconciliation,
            impact.Unchanged,
            userId.Value,
            now);
        domainWriteContext.Add(operation);
        if (policySet is null)
        {
            domainWriteContext.Add(RoleVaultAccessPolicySet.Create(
                organizationId.Value,
                req.RoleId,
                requestedVaultIds,
                operation.Id,
                userId.Value,
                authorizedRoleRevision,
                authorizedByPermissions,
                now));
        }
        else
        {
            policySet.BindRoleAuthorization(authorizedRoleRevision, authorizedByPermissions);
            policySet.Replace(requestedVaultIds, operation.Id, userId.Value, now);
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            await SendConflictAsync(ct);
            return;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
               { SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            await SendConflictAsync(ct);
            return;
        }

        await Send.ResponseAsync(ToResponse(operation), 202, ct);
    }

    private Task SendConflictAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("role-vault-access-conflict"));
        return Send.ErrorsAsync(409, ct);
    }

    private static UpdateRoleVaultAccessResponse ToResponse(RoleVaultAccessOperation operation) =>
        new(
            operation.Id,
            RoleVaultAccessStatus.From(operation.Status),
            new RoleVaultAccessImpact(
                operation.AddsAwaitingProvisioning,
                operation.RemovalsAwaitingSourceReconciliation,
                operation.Unchanged));
}
