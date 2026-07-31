using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record DenyGrantRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

[UsedImplicitly]
internal sealed class DenyGrantValidator : Validator<DenyGrantRequest>
{
    public DenyGrantValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class DenyGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<DenyGrantRequest>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/grants/{grantId:guid}/deny");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Deny a pending grant";
            summary.Description = "Rejects a pending agent access request and deletes its encrypted reason. Caller must be a member of the vault.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(DenyGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;

        var grant = await domainWriteContext.Grants
            .Include(g => g.EncryptedReason)
            .Include(g => g.GrantEntryScopes).ThenInclude(scope => scope.Envelope)
            .FirstOrDefaultAsync(g => g.Id == req.GrantId && g.VaultId == req.VaultId, ct);
        if (grant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var names = await domainReadContext.ResolveAsync(
            grant.AgentId, (grant as GranularGrant)?.EntryId, grant.VaultId, userId, ct);
        grant.Deny(userId, names, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
