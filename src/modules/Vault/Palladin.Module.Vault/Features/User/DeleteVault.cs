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

        var vault = await domainWriteContext.Vaults
            .IgnoreQueryFilters()
            .Include(v => v.VaultMembers)
            .FirstOrDefaultAsync(v => v.Id == req.Id && v.OrganizationId == organizationId, ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!vault.IsDeleting)
        {
            vault.BeginDeletion(userId, User.GetDisplayName(), clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
        }

        // Object storage is outside the database boundary. IsDeleting hides the Vault and makes
        // this phase safely retryable without holding locks during S3 calls.
        await assetPurger.PurgeVaultAsync(organizationId, vault.Id, ct);

        domainWriteContext.Clear();
        vault = await domainWriteContext.Vaults
            .IgnoreQueryFilters()
            .Include(v => v.VaultMembers)
            .SingleOrDefaultAsync(v => v.Id == req.Id && v.OrganizationId == organizationId, ct);
        if (vault is not null)
        {
            vault.CompleteDeletion();
            domainWriteContext.Remove(vault);
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}
