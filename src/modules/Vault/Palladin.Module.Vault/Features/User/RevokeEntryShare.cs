using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RevokeEntryShareRequest(Guid VaultId, Guid EntryId, Guid ShareId);

[UsedImplicitly]
internal sealed class RevokeEntryShareValidator : Validator<RevokeEntryShareRequest>
{
    public RevokeEntryShareValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.ShareId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class RevokeEntryShareEndpoint(VaultDomainWriteContext context, IClock clock)
    : Endpoint<RevokeEntryShareRequest>
{
    public override void Configure()
    {
        Delete("api/vaults/{vaultId:guid}/entries/{entryId:guid}/sharing/{shareId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "Permanently revoke a sharing link created by the current Member");
    }

    public override async Task HandleAsync(RevokeEntryShareRequest req, CancellationToken ct)
   {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var vault = await context.Vaults.SingleOrDefaultAsync(x => x.OrganizationId == organizationId
            && x.Id == req.VaultId && x.VaultMembers.Any(m => m.UserId == userId), ct);
        var share = await context.EntryShares.SingleOrDefaultAsync(x => x.OrganizationId == organizationId
            && x.VaultId == req.VaultId && x.EntryId == req.EntryId && x.Id == req.ShareId && x.CreatedBy == userId, ct);
        if (vault is null || share is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        context.MarkPropertyAsUpdated(vault, x => x.MutationVersion);
        share.Revoke(EntryShareActivityKind.RevokedBySender, clock.GetCurrentInstant());
        await context.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
