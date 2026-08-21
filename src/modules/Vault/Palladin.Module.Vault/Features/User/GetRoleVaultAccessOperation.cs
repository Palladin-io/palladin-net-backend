using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetRoleVaultAccessOperationRequest
{
    public Guid OperationId { get; init; }
}

[PublicAPI]
public sealed record GetRoleVaultAccessOperationResponse(
    Guid OperationId,
    Guid RoleId,
    string Status,
    RoleVaultAccessImpact Impact,
    Instant CreatedAt,
    Instant UpdatedAt);

[UsedImplicitly]
internal sealed class GetRoleVaultAccessOperationValidator : Validator<GetRoleVaultAccessOperationRequest>
{
    public GetRoleVaultAccessOperationValidator() => RuleFor(x => x.OperationId).NotEmpty();
}

[PublicAPI]
[RequireActiveOrganizationMembership]
internal sealed class GetRoleVaultAccessOperationEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<GetRoleVaultAccessOperationRequest, GetRoleVaultAccessOperationResponse>
{
    public override void Configure()
    {
        Get("api/organization/role-vault-access-operations/{operationId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement | Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Role access");
        Summary(summary =>
        {
            summary.Summary = "Get a role Vault access operation";
            summary.Description = "Returns structural desired-state progress only and never returns key material.";
        });
    }

    public override async Task HandleAsync(GetRoleVaultAccessOperationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var response = await domainReadContext.RoleVaultAccessOperations
            .Where(x => x.OrganizationId == organizationId && x.Id == req.OperationId)
            .Select(x => new GetRoleVaultAccessOperationResponse(
                x.Id,
                x.RoleId,
                x.Status == RoleVaultAccessOperationStatus.AwaitingProvisioner
                    ? RoleVaultAccessStatus.AwaitingProvisioner
                    : RoleVaultAccessStatus.Superseded,
                new RoleVaultAccessImpact(
                    x.AddsAwaitingProvisioning,
                    x.RemovalsAwaitingSourceReconciliation,
                    x.Unchanged),
                x.CreatedAt,
                x.UpdatedAt))
            .SingleOrDefaultAsync(ct);
        if (response is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }


        var selectedVaultIds = await domainReadContext.RoleVaultAccessPolicies
            .Where(x => x.OrganizationId == organizationId && x.RoleId == response.RoleId)
            .Select(x => x.VaultId)
            .ToArrayAsync(ct);
        var callerMembershipCount = await domainReadContext.VaultMembers.CountAsync(
            x => x.OrganizationId == organizationId
                 && x.UserId == userId
                 && selectedVaultIds.Contains(x.VaultId),
            ct);
        if (callerMembershipCount != selectedVaultIds.Length)
        {
            AddError(ErrorResponses.General("role-vault-access-selection-invalid"));
            await Send.ErrorsAsync(404, ct);
            return;
        }

        await Send.OkAsync(response, ct);
    }
}
