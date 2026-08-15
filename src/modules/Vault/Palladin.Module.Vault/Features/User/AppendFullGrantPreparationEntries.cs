using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record AppendFullGrantPreparationEntriesRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEntries { get; init; } = [];
}

[PublicAPI]
public sealed record AppendFullGrantPreparationEntriesResponse(
    int AcceptedEntries,
    int TotalPreparedEntries);

[UsedImplicitly]
internal sealed class AppendFullGrantPreparationEntriesValidator
    : Validator<AppendFullGrantPreparationEntriesRequest>
{
    public AppendFullGrantPreparationEntriesValidator(IOptions<VaultCryptoOptions> cryptoOptions)
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.GrantEntries)
            .NotEmpty()
            .Must(x => x.Count <= cryptoOptions.Value.MaxFullGrantPreparationBatchEntries);
        RuleFor(x => x.GrantEntries)
            .Must(x => x.Select(entry => entry.EntryId).Distinct().Count() == x.Count);
        RuleForEach(x => x.GrantEntries).SetValidator(new GrantEntryEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class AppendFullGrantPreparationEntriesEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<AppendFullGrantPreparationEntriesRequest, AppendFullGrantPreparationEntriesResponse>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/grants/full/preparations/{grantId:guid}/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Append one bounded FULL grant envelope batch";
            summary.Description = "Validates and stores opaque per-Entry grant envelopes without activating the grant. Exact retries are idempotent; changed retries fail closed.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(AppendFullGrantPreparationEntriesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(organizationId).SingleAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId).SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var preparation = await domainWriteContext.FullGrantPreparations.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.Id == req.GrantId, ct);
        if (preparation is null || preparation.CreatedBy != userId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (now >= preparation.PreparationExpiresAt)
        {
            domainWriteContext.Remove(preparation);
            await domainWriteContext.CommitAsync(transaction, ct);
            AddError(ErrorResponses.General("full-grant-preparation-expired"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var currentAgent = await domainWriteContext.LockAgent(organizationId, preparation.AgentId)
            .SingleOrDefaultAsync(ct);
        if (currentAgent is null
            || currentAgent.Status != AgentStatus.Active
            || currentAgent.AccessEpoch != preparation.AgentAccessEpoch
            || currentAgent.RecipientKeyVersion != preparation.RecipientAgentKeyVersion
            || !string.Equals(currentAgent.PublicKey, preparation.AgentPublicKey, StringComparison.Ordinal)
            || vault.MemberKeyGeneration.Value != preparation.MemberKeyGeneration
            || await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == preparation.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError(ErrorResponses.General("full-grant-preparation-agent-changed"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var entryIds = req.GrantEntries.Select(x => x.EntryId).Order().ToArray();
        var lockedEntries = await domainWriteContext.LockEntries(organizationId, req.VaultId, entryIds)
            .ToDictionaryAsync(x => x.Id, ct);
        if (lockedEntries.Count != entryIds.Length
            || lockedEntries.Values.Any(entry => entry.State != EntryState.Active))
        {
            AddError(r => r.GrantEntries, "One or more entries are no longer active.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        var revisions = lockedEntries.ToDictionary(x => x.Key, x => x.Value.CurrentRevision.Value);

        var existing = await domainWriteContext.FullGrantPreparationEntries
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && x.PreparationId == req.GrantId
                        && entryIds.Contains(x.EntryId))
            .ToDictionaryAsync(x => x.EntryId, ct);
        var accepted = 0;
        foreach (var contract in req.GrantEntries)
        {
            FullGrantPreparationEntry candidate;
            try
            {
                var scope = FullGrantPreparationEnvelopeValidator.Validate(
                    contract, preparation, revisions[contract.EntryId]);
                candidate = FullGrantPreparationEntry.Create(
                    preparation,
                    scope,
                    VaultPreparedPayloadCodec.Encode(contract),
                    now);
            }
            catch (Exception ex) when (ex is FormatException or DomainException or OverflowException)
            {
                AddError(r => r.GrantEntries, "Grant envelope is invalid or stale.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            if (existing.TryGetValue(contract.EntryId, out var prepared))
            {
                if (!prepared.HasSameContent(candidate))
                {
                    AddError(r => r.GrantEntries, "A prepared Entry retry changed authenticated content.");
                    await Send.ErrorsAsync(409, ct);
                    return;
                }

                continue;
            }

            domainWriteContext.Add(candidate);
            accepted++;
        }

        var previousTotal = await domainWriteContext.FullGrantPreparationEntries.CountAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.PreparationId == req.GrantId, ct);
        await domainWriteContext.CommitAsync(transaction, ct);
        await Send.OkAsync(new AppendFullGrantPreparationEntriesResponse(
            accepted,
            previousTotal + accepted), ct);
    }

}
