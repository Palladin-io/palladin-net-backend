using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ChangeEntryShareProtectionRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public Guid ShareId { get; init; }
    public EntryShareProtection Protection { get; init; }
    public string? ProtectionSecret { get; init; }
    public override string ToString() => nameof(ChangeEntryShareProtectionRequest);
}

[UsedImplicitly]
internal sealed class ChangeEntryShareProtectionValidator : Validator<ChangeEntryShareProtectionRequest>
{
    public ChangeEntryShareProtectionValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.Protection).IsInEnum();
        RuleFor(x => x.ProtectionSecret).MaximumLength(256);
    }
}

[PublicAPI]
internal sealed class ChangeEntryShareProtectionEndpoint(
    VaultDomainWriteContext context, EntryShareAuthority authority, EntryShareSecurity security, IClock clock)
    : Endpoint<ChangeEntryShareProtectionRequest>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/entries/{entryId:guid}/sharing/{shareId:guid}/protection");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "Change optional sharing protection and invalidate prior verification");
    }

    public override async Task HandleAsync(ChangeEntryShareProtectionRequest req, CancellationToken ct)
   {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var scope = new EntryScope(User.GetOrganizationId()!.Value, req.VaultId, req.EntryId);
        var userId = User.GetUserId()!.Value;
        await authority.LoadSenderSourceAsync(scope, userId, User.GetAuthorizationVersion()!.Value, ct);
        var share = await context.EntryShares.SingleOrDefaultAsync(x => x.OrganizationId == scope.OrganizationId
            && x.VaultId == scope.VaultId && x.EntryId == scope.EntryId && x.Id == req.ShareId && x.CreatedBy == userId, ct);
        if (share is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await authority.EnsureRecipientSourceAsync(share, ct);
        share.ChangeProtection(req.Protection,
            security.CreateSecretVerifier(share.Id, req.Protection, req.ProtectionSecret), clock.GetCurrentInstant());
        await context.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
