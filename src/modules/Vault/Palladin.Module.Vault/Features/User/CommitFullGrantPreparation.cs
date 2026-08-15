using System.Text.Json;
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
public sealed record CommitFullGrantPreparationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

[UsedImplicitly]
internal sealed class CommitFullGrantPreparationValidator : Validator<CommitFullGrantPreparationRequest>
{
    public CommitFullGrantPreparationValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class CommitFullGrantPreparationEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IOptions<VaultCryptoOptions> cryptoOptions,
    IClock clock) : Endpoint<CommitFullGrantPreparationRequest, CreateGrantResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/grants/full/preparations/{grantId:guid}/commit");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Atomically commit a prepared FULL grant";
            summary.Description = "Activates one FULL grant only when every current active Entry has exactly one current revision-bound prepared envelope and all Vault and Agent key bindings remain unchanged.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CommitFullGrantPreparationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var existingGrant = await domainReadContext.Grants.OfType<FullGrant>()
            .SingleOrDefaultAsync(g => g.Id == req.GrantId
                                       && g.OrganizationId == organizationId
                                       && g.VaultId == req.VaultId, ct);
        if (existingGrant is { Status: GrantStatus.Active, CreatedBy: var createdBy }
            && createdBy == userId)
        {
            await Send.CreatedAtAsync<GetGrantEndpoint>(
                new { vaultId = req.VaultId, grantId = req.GrantId },
                new CreateGrantResponse(req.GrantId),
                cancellation: ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(organizationId).SingleAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .SingleOrDefaultAsync(ct);
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

        var agent = await domainWriteContext.LockAgent(organizationId, preparation.AgentId)
            .SingleOrDefaultAsync(ct);
        if (agent is null
            || agent.Status != AgentStatus.Active
            || agent.AccessEpoch != preparation.AgentAccessEpoch
            || agent.RecipientKeyVersion != preparation.RecipientAgentKeyVersion
            || !string.Equals(agent.PublicKey, preparation.AgentPublicKey, StringComparison.Ordinal)
            || !VaultKeyFingerprint.Compute(Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519)
                .AsSpan().SequenceEqual(preparation.AgentKeyFingerprint)
            || vault.MemberKeyGeneration.Value != preparation.MemberKeyGeneration
            || await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == preparation.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError(ErrorResponses.General("full-grant-preparation-context-changed"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (await domainWriteContext.Grants.AnyAsync(g => g.Id == req.GrantId, ct)
            || await domainWriteContext.Grants.OfType<FullGrant>().AnyAsync(g =>
                g.OrganizationId == organizationId
                && g.VaultId == req.VaultId
                && g.AgentId == preparation.AgentId
                && g.AgentAccessEpoch == preparation.AgentAccessEpoch
                && g.Status == GrantStatus.Active, ct))
        {
            AddError(ErrorResponses.General("full-grant-active-coverage"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (await domainWriteContext.FullGrantPreparationSnapshotMismatchCount(
                organizationId, req.VaultId, req.GrantId).SingleAsync(ct) != 0)
        {
            AddError(ErrorResponses.General("full-grant-preparation-snapshot-changed"));
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var grant = FullGrant.CreateForPreparation(preparation, userId, now);
        domainWriteContext.Add(grant);
        await domainWriteContext.FlushAsync(ct);
        domainWriteContext.Clear();

        Guid? afterEntryId = null;
        while (true)
        {
            var preparedQuery = domainWriteContext.FullGrantPreparationEntries.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId
                            && x.VaultId == req.VaultId
                            && x.PreparationId == req.GrantId);
            if (afterEntryId is { } after)
            {
                preparedQuery = preparedQuery.Where(x => x.EntryId.CompareTo(after) > 0);
            }

            var page = await preparedQuery
                .OrderBy(x => x.EntryId)
                .Take(cryptoOptions.Value.MaxFullGrantPreparationBatchEntries)
                .ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }

            var trackedGrant = await domainWriteContext.Grants.OfType<FullGrant>()
                .SingleAsync(x => x.Id == req.GrantId, ct);
            try
            {
                foreach (var item in page)
                {
                    var contract = VaultPreparedPayloadCodec.Decode<GrantEntryEnvelopeContract>(item.Payload);
                    trackedGrant.WrapNewEntry(FullGrantPreparationEnvelopeValidator.Validate(
                        contract, preparation, item.EntryRevision));
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or DomainException or OverflowException)
            {
                AddError(ErrorResponses.General("full-grant-preparation-invalid"));
                await Send.ErrorsAsync(409, ct);
                return;
            }

            afterEntryId = page[^1].EntryId;
            await domainWriteContext.FlushAsync(ct);
            domainWriteContext.Clear();
        }

        Guid? afterGrantId = null;
        while (true)
        {
            var superseded = await domainWriteContext.LoadActiveGranularInVaultPageAsync(
                preparation.AgentId,
                preparation.AgentAccessEpoch,
                req.VaultId,
                afterGrantId,
                cryptoOptions.Value.MaxFullGrantPreparationBatchEntries,
                ct);
            if (superseded.Count == 0)
            {
                break;
            }

            var entryIds = superseded.Select(x => x.EntryId).Distinct().ToArray();
            var namesByEntry = await domainReadContext.ResolveForSupersedeAsync(
                preparation.AgentId, req.VaultId, entryIds, ct);
            foreach (var granular in superseded)
            {
                granular.RevokeBySystem(namesByEntry[granular.EntryId], now);
            }

            afterGrantId = superseded[^1].Id;
            domainWriteContext.EnsureFullGrantCommitTrackingIsBounded(
                cryptoOptions.Value.MaxFullGrantPreparationBatchEntries);
            await domainWriteContext.FlushAsync(ct);
            domainWriteContext.Clear();
        }

        var completedGrant = await domainWriteContext.Grants.OfType<FullGrant>()
            .SingleAsync(x => x.Id == req.GrantId, ct);
        completedGrant.CompletePreparation(
            await domainReadContext.ResolveAsync(
                preparation.AgentId, null, req.VaultId, userId, ct),
            now);
        var completedPreparation = await domainWriteContext.FullGrantPreparations.SingleAsync(x =>
            x.OrganizationId == organizationId
            && x.VaultId == req.VaultId
            && x.Id == req.GrantId, ct);
        domainWriteContext.Remove(completedPreparation);
        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = req.GrantId },
            new CreateGrantResponse(req.GrantId),
            cancellation: ct);
    }
}
