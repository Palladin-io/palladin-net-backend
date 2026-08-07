using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record PurgeEntryHistoryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string BeforeRevision { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record PurgeEntryHistoryResponse(int RemovedVersions);

[UsedImplicitly]
internal sealed class PurgeEntryHistoryValidator : Validator<PurgeEntryHistoryRequest>
{
    public PurgeEntryHistoryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.BeforeRevision).NotEmpty();
    }
}

[PublicAPI]
internal sealed class PurgeEntryHistoryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<PurgeEntryHistoryRequest, PurgeEntryHistoryResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/{entryId:guid}/history/purge");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Purge older encrypted Entry history";
            summary.Description = "Permanently removes immutable versions below beforeRevision while retaining the current head and advances sync retention floors atomically.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(PurgeEntryHistoryRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var entry = await domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId
                                       && x.VaultId == req.VaultId
                                       && x.Id == req.EntryId, ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var beforeRevision = VaultEnvelopeContractMapper.ToEntryRevision(req.BeforeRevision);
        if (beforeRevision.Value > entry.CurrentRevision.Value)
        {
            AddError("beforeRevision cannot exceed the current Entry revision.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var removed = entry.PurgeHistory(beforeRevision, null);
        if (removed.RemovedVersions > 0)
        {
            domainWriteContext.RemoveRange(removed.Versions);
            domainWriteContext.RemoveRange(removed.Keys);
            var vault = await domainWriteContext.Vaults.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == req.VaultId,
                ct);
            vault.AdvanceRetentionFloors(
                new MemberSequence(Math.Max(
                    vault.MinRetainedMemberSequence.Value,
                    removed.MemberSequenceFloor)),
                new DiscoverySequence(Math.Max(
                    vault.MinRetainedDiscoverySequence.Value,
                    removed.DiscoverySequenceFloor)),
                User.GetUserId()!.Value,
                clock.GetCurrentInstant());
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.OkAsync(new PurgeEntryHistoryResponse(removed.RemovedVersions), ct);
    }
}
