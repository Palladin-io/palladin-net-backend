using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record UpdateVaultRequest
{
    public Guid Id { get; init; }
    public MemberVaultMetadataEnvelopeContract MemberVaultMetadata { get; init; } = null!;
}

[UsedImplicitly]
internal sealed class UpdateVaultValidator : Validator<UpdateVaultRequest>
{
    public UpdateVaultValidator() =>
        RuleFor(x => x.MemberVaultMetadata).NotNull().SetValidator(new MemberVaultMetadataContractValidator()!);
}

[PublicAPI]
internal sealed class UpdateVaultEndpoint(VaultDomainWriteContext domainWriteContext, IClock clock)
    : Endpoint<UpdateVaultRequest>
{
    public override void Configure()
    {
        Put("api/vaults/{id:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        this.RequirePermission(Permission.VaultManage);
        Summary(summary =>
        {
            summary.Summary = "Replace encrypted Vault metadata";
            summary.Description = "Replaces the authenticated ciphertext when its revision advances monotonically. The caller must have VaultManage and be a structural Vault Member.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(UpdateVaultRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await domainWriteContext.Vaults
            .Where(v => v.OrganizationId == organizationId && v.Id == req.Id)
            .Where(v => v.VaultMembers.Any(m => m.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        vault.ReplaceMetadata(
            userId,
            User.GetDisplayName(),
            VaultEnvelopeContractMapper.ToDomain(req.MemberVaultMetadata),
            clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
