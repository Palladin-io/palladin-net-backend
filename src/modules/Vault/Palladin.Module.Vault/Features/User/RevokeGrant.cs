using Palladin.Core.Security;
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
public sealed record RevokeGrantRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

[UsedImplicitly]
internal sealed class RevokeGrantValidator : Validator<RevokeGrantRequest>
{
    public RevokeGrantValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class RevokeGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<RevokeGrantRequest>
{
    public override void Configure()
    {
        Delete("api/vaults/{vaultId:guid}/grants/{grantId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Revoke a grant";
            summary.Description = "Sets the grant status to Revoked and hard-deletes secret grant envelopes. Caller must be a member of the vault.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(RevokeGrantRequest req, CancellationToken ct)
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
        grant.Revoke(userId, names, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
