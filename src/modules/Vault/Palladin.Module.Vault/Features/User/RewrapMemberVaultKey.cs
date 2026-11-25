using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RewrapMemberVaultKeyRequest
{
    public Guid VaultId { get; init; }
    public uint ExpectedRecipientKeyVersion { get; init; }
    public MemberVaultKeyEnvelopeContract WrappedVaultKey { get; init; } = null!;
}

[PublicAPI]
public sealed record RewrapMemberVaultKeyResponse(MemberVaultKeyEnvelopeContract WrappedVaultKey);

[UsedImplicitly]
internal sealed class RewrapMemberVaultKeyValidator : Validator<RewrapMemberVaultKeyRequest>
{
    public RewrapMemberVaultKeyValidator()
    {
        RuleFor(x => x.ExpectedRecipientKeyVersion).GreaterThan(0u);
        RuleFor(x => x.WrappedVaultKey).NotNull().SetValidator(new MemberVaultKeyContractValidator()!);
    }
}

[PublicAPI]
internal sealed class RewrapMemberVaultKeyEndpoint(VaultDomainWriteContext domainWriteContext, IClock clock)
    : Endpoint<RewrapMemberVaultKeyRequest, RewrapMemberVaultKeyResponse>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/members/me/key-envelope");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Re-wrap the caller's current Vault key to their current Member key";
            summary.Description = "Replaces the caller's wrapper for the current Vault key version and Member-key "
                + "generation with one sealed to the Member key currently registered in the authenticated key "
                + "directory, using its key-wrapper suite. The stored recipient key version must equal "
                + "expectedRecipientKeyVersion and the new one must be newer. An identical retry is idempotent.";
        });
        Tags("Vault/Members");
    }

    public override async Task HandleAsync(RewrapMemberVaultKeyRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await domainWriteContext.Vaults
            .Include(v => v.VaultMemberKeyEnvelopes.Where(x => x.MemberId == userId))
            .Where(v => v.OrganizationId == organizationId && v.Id == req.VaultId)
            .Where(v => v.VaultMembers.Any(m => m.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var wrappedVaultKey = VaultEnvelopeContractMapper.ToDomain(req.WrappedVaultKey);
        var directory = await domainWriteContext.MemberKeyDirectory
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        CreateVaultEndpoint.ValidateCreatorKeyDirectory(directory, wrappedVaultKey);

        if (vault.RewrapMemberKey(
                userId,
                wrappedVaultKey,
                new MemberRecipientKeyVersion(req.ExpectedRecipientKeyVersion),
                clock.GetCurrentInstant()))
        {
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.OkAsync(new RewrapMemberVaultKeyResponse(VaultEnvelopeContractMapper.ToContract(wrappedVaultKey)), ct);
    }
}
