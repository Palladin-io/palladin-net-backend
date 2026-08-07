using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Purge;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record DestroyEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
}

[UsedImplicitly]
internal sealed class DestroyEntryValidator : Validator<DestroyEntryRequest>
{
    public DestroyEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class DestroyEntryEndpoint(EntryPurgeService purgeService) : Endpoint<DestroyEntryRequest>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/{entryId:guid}/destroy");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Destroy a Recently Deleted Entry permanently";
            summary.Description = "Appends the rollback-resistant opaque purge ledger record before permanently removing all Entry ciphertext, wrapped keys, versions and grant material.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(DestroyEntryRequest req, CancellationToken ct)
    {
        var result = await purgeService.PurgeAsync(
            new EntryScope(User.GetOrganizationId()!.Value, req.VaultId, req.EntryId),
            User.GetUserId()!.Value,
            null,
            appendLedger: true,
            requireDeleted: true,
            ct);
        if (result == EntryPurgeResult.NotEligible)
        {
            AddError("Only a Recently Deleted Entry can be destroyed permanently.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
