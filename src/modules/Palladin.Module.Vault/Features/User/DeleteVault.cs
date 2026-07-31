using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record DeleteVaultRequest
{
    public Guid Id { get; init; }
}

[PublicAPI]
internal sealed class DeleteVaultEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IEntryAssetPurger assetPurger,
    IClock clock) : Endpoint<DeleteVaultRequest>
{
    public override void Configure()
    {
        Delete("api/vaults/{id:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Delete a vault";
            summary.Description = "Permanently removes the vault and its membership rows. Returns 404 when the vault does not belong to the caller's organization.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(DeleteVaultRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var lockedVault = await domainWriteContext.LockVault(organizationId, req.Id)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var vault = await domainWriteContext.Vaults
            .Include(v => v.VaultMembers)
            .FirstOrDefaultAsync(v => v.Id == req.Id && v.OrganizationId == organizationId, ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();

        vault.Delete(userId, User.GetDisplayName(), now);
        await assetPurger.PurgeVaultAsync(organizationId, vault.Id, ct);
        domainWriteContext.Remove(vault);

        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.NoContentAsync(ct);
    }
}
