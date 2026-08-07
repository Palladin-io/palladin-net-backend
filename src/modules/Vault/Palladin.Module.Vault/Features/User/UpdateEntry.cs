using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record UpdateEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string BaseRevision { get; init; } = string.Empty;
    public VaultEntryKeyContract? NewEntryKey { get; init; }
    public MemberSecretEnvelopeContract MemberSecret { get; init; } = null!;
    public MemberIndexEnvelopeContract? MemberIndex { get; init; }
    public bool AgentDiscoveryChanged { get; init; }
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEnvelopes { get; init; } = [];
}

[PublicAPI]
public sealed record UpdateEntryResponse(string CurrentRevision);

[UsedImplicitly]
internal sealed class UpdateEntryValidator : Validator<UpdateEntryRequest>
{
    public UpdateEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.BaseRevision).NotEmpty();
        RuleFor(x => x.MemberSecret).NotNull();
        RuleFor(x => x.AgentDiscovery).Null().When(x => !x.AgentDiscoveryChanged);
        RuleForEach(x => x.GrantEnvelopes).SetValidator(new GrantEntryEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class UpdateEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateEntryRequest, UpdateEntryResponse>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/entries/{entryId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Update a canonical encrypted Entry";
            summary.Description = "Creates one immutable canonical MemberSecret version and atomically switches every changed projection head using optimistic baseRevision concurrency. The backend never merges ciphertext.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(UpdateEntryRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var baseRevision = VaultEnvelopeContractMapper.ToEntryRevision(req.BaseRevision);
        var memberSecret = VaultEnvelopeContractMapper.ToDomain(req.MemberSecret);
        var memberIndex = req.MemberIndex is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.MemberIndex);
        var agentDiscovery = req.AgentDiscovery is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.AgentDiscovery);
        var newKey = req.NewEntryKey is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.NewEntryKey);

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var lockedVault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await domainWriteContext.LockVaultGrantIds(organizationId, req.VaultId).ToListAsync(ct);
        var entry = await domainWriteContext.LockEntry(organizationId, req.VaultId, req.EntryId)
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entry.IsExactUpdateRetry(
                baseRevision,
                newKey,
                memberSecret,
                memberIndex,
                req.AgentDiscoveryChanged,
                agentDiscovery))
        {
            await Send.OkAsync(new UpdateEntryResponse(entry.CurrentRevision.Value.ToString()), ct);
            return;
        }

        var activeGrants = await domainWriteContext.Grants
            .Include(g => g.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Where(
            g => g.OrganizationId == organizationId
                 && g.VaultId == req.VaultId
                 && g.Status == GrantStatus.Active
                 && g.GrantEntryScopes.Any(scope => scope.EntryId == req.EntryId
                                                    && scope.Envelope != null))
            .ToListAsync(ct);
        var scopes = activeGrants.SelectMany(g => g.GrantEntryScopes)
            .Where(scope => scope.EntryId == req.EntryId
                            && scope.Envelope is not null)
            .ToArray();
        if (!scopes.Select(scope => scope.GrantId).Order().SequenceEqual(
                req.GrantEnvelopes.Select(envelope => envelope.GrantId).Order()))
        {
            AddError(r => r.GrantEnvelopes, "Every active covering grant must be refreshed exactly once.");
            await Send.StatusCodeAsync(409, ct);
            return;
        }

        var vault = await domainWriteContext.Vaults
            .FirstAsync(x => x.OrganizationId == entry.OrganizationId && x.Id == entry.VaultId, ct);
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        var sequences = vault.AllocateSequences(req.AgentDiscoveryChanged, userId, now);

        entry.Update(
            baseRevision,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            newKey,
            memberSecret,
            memberIndex,
            req.AgentDiscoveryChanged,
            agentDiscovery,
            sequences,
            now,
            userId);

        var agents = await domainWriteContext.Agents
            .Where(agent => activeGrants.Select(grant => grant.AgentId).Contains(agent.Id)
                            && agent.OrganizationId == organizationId)
            .ToDictionaryAsync(agent => agent.Id, ct);
        try
        {
            foreach (var scope in scopes)
            {
                var contract = req.GrantEnvelopes.Single(x => x.GrantId == scope.GrantId);
                var grant = activeGrants.Single(x => x.Id == scope.GrantId);
                if (contract.OrganizationId != organizationId || contract.VaultId != req.VaultId
                    || contract.EntryId != req.EntryId
                    || contract.EntryRevision != entry.CurrentRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || contract.MemberKeyGeneration != vault.MemberKeyGeneration.Value
                    || contract.RecipientAgentKeyVersion != agents[grant.AgentId].RecipientKeyVersion
                    || contract.ExpiresAt != grant.ExpiresAt
                    || contract.RemainingUses != (grant.QueryLimit is null ? null : grant.QueryLimit - grant.QueryCount))
                {
                    throw new Palladin.Core.Types.Exceptions.DomainException("Grant refresh scope is invalid.");
                }
                var refreshed = GrantEnvelopeContractMapper.ToDomain(contract, scope.Methods, grant.AgentId);
                var fingerprint = VaultKeyFingerprint.Compute(
                    Convert.FromBase64String(agents[grant.AgentId].PublicKey), VaultKeyKind.AgentX25519);
                if (!refreshed.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
                {
                    throw new Palladin.Core.Types.Exceptions.DomainException("Grant refresh Agent key is invalid.");
                }
                scope.NarrowAndRefresh(refreshed);
            }
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(r => r.GrantEnvelopes, "Grant envelope refresh is invalid or stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await domainWriteContext.CommitAsync(transaction, ct);
        await Send.OkAsync(new UpdateEntryResponse(entry.CurrentRevision.Value.ToString()), ct);
    }
}
