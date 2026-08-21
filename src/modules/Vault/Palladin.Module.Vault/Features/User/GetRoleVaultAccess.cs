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
public sealed record GetRoleVaultAccessRequest
{
    public Guid RoleId { get; init; }
}

[PublicAPI]
public sealed record RoleVaultAccessImpact(
    int AddsAwaitingProvisioning,
    int RemovalsAwaitingSourceReconciliation,
    int Unchanged);

[PublicAPI]
public sealed record PendingRoleVaultAccessOperation(
    Guid OperationId,
    string Status,
    RoleVaultAccessImpact Impact);

[PublicAPI]
public sealed record GetRoleVaultAccessResponse(
    Guid RoleId,
    IReadOnlyList<Guid> SelectedVaultIds,
    PendingRoleVaultAccessOperation? PendingOperation);

[UsedImplicitly]
internal sealed class GetRoleVaultAccessValidator : Validator<GetRoleVaultAccessRequest>
{
    public GetRoleVaultAccessValidator() => RuleFor(x => x.RoleId).NotEmpty();
}

[PublicAPI]
[RequireActiveOrganizationMembership]
internal sealed class GetRoleVaultAccessEndpoint(VaultDomainReadContext domainReadContext)
    : Endpoint<GetRoleVaultAccessRequest, GetRoleVaultAccessResponse>
{
    public override void Configure()
    {
        Get("api/organization/roles/{roleId}/vault-access");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement | Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Role access");
        Summary(summary =>
        {
            summary.Summary = "Get desired Vault access for an organization role";
            summary.Description = "Returns opaque selected Vault identifiers and pending structural status. Vault presentation metadata remains client-encrypted.";
        });
    }

    public override async Task HandleAsync(GetRoleVaultAccessRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var roleExists = await domainReadContext.OrganizationRoleDirectory.AnyAsync(
            x => x.OrganizationId == organizationId && x.RoleId == req.RoleId && !x.IsDeleted,
            ct);
        if (!roleExists)
        {
            AddError(ErrorResponses.General("role-vault-access-role-unavailable"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var policy = await domainReadContext.RoleVaultAccessPolicySets
            .Where(x => x.OrganizationId == organizationId && x.RoleId == req.RoleId)
            .Select(x => new
            {
                VaultIds = x.Policies.Select(policy => policy.VaultId).Order().ToArray(),
                x.LastOperationId,
            })
            .SingleOrDefaultAsync(ct);

        var selectedVaultIds = policy?.VaultIds ?? [];
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

        PendingRoleVaultAccessOperation? pendingOperation = null;
        if (policy?.LastOperationId is { } operationId)
        {
            pendingOperation = await domainReadContext.RoleVaultAccessOperations
                .Where(x => x.OrganizationId == organizationId
                            && x.Id == operationId
                            && x.Status == RoleVaultAccessOperationStatus.AwaitingProvisioner)
                .Select(x => new PendingRoleVaultAccessOperation(
                    x.Id,
                    RoleVaultAccessStatus.AwaitingProvisioner,
                    new RoleVaultAccessImpact(
                        x.AddsAwaitingProvisioning,
                        x.RemovalsAwaitingSourceReconciliation,
                        x.Unchanged)))
                .SingleOrDefaultAsync(ct);
        }

        await Send.OkAsync(new GetRoleVaultAccessResponse(
            req.RoleId,
            selectedVaultIds,
            pendingOperation), ct);
    }
}

internal static class RoleVaultAccessStatus
{
    internal const string AwaitingProvisioner = "awaiting-provisioner";
    internal const string Superseded = "superseded";
    internal const string Unchanged = "unchanged";

    internal static string From(RoleVaultAccessOperationStatus status) => status switch
    {
        RoleVaultAccessOperationStatus.AwaitingProvisioner => AwaitingProvisioner,
        RoleVaultAccessOperationStatus.Superseded => Superseded,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
