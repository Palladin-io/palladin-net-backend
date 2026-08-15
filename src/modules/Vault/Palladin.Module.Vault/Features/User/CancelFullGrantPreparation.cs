using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CancelFullGrantPreparationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

[UsedImplicitly]
internal sealed class CancelFullGrantPreparationValidator : Validator<CancelFullGrantPreparationRequest>
{
    public CancelFullGrantPreparationValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class CancelFullGrantPreparationEndpoint(VaultDomainWriteContext domainWriteContext)
    : Endpoint<CancelFullGrantPreparationRequest>
{
    public override void Configure()
    {
        Delete("api/vaults/{vaultId:guid}/grants/full/preparations/{grantId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Cancel a FULL grant preparation";
            summary.Description = "Deletes only staged encrypted material. No active grant or existing access is changed.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CancelFullGrantPreparationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        if (await domainWriteContext.LockVault(organizationId, req.VaultId).SingleOrDefaultAsync(ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var preparation = await domainWriteContext.FullGrantPreparations.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.Id == req.GrantId
            && x.CreatedBy == userId, ct);
        if (preparation is not null)
        {
            domainWriteContext.Remove(preparation);
            await domainWriteContext.CommitAsync(transaction, ct);
        }

        await Send.NoContentAsync(ct);
    }
}
