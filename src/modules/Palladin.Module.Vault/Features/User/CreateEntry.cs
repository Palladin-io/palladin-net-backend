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
public sealed record CreateEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public VaultEntryKeyContract EntryKey { get; init; } = null!;
    public MemberIndexEnvelopeContract MemberIndex { get; init; } = null!;
    public MemberSecretEnvelopeContract MemberSecret { get; init; } = null!;
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEnvelopes { get; init; } = [];
}

[PublicAPI]
public sealed record CreateEntryResponse(Guid Id, string CurrentRevision);

[UsedImplicitly]
internal sealed class CreateEntryValidator : Validator<CreateEntryRequest>
{
    public CreateEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.EntryKey).NotNull();
        RuleFor(x => x.MemberIndex).NotNull();
        RuleFor(x => x.MemberSecret).NotNull();
        RuleForEach(x => x.GrantEnvelopes).SetValidator(new GrantEntryEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class CreateEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateEntryRequest, CreateEntryResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a canonical encrypted Entry";
            summary.Description = "Atomically stores the Entry head, wrapped EntryDEK, canonical MemberSecret version, MemberIndex projection and optional AgentDiscovery projection. All presentation fields remain encrypted on the client.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(CreateEntryRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var entryKey = VaultEnvelopeContractMapper.ToDomain(req.EntryKey);
        var memberIndex = VaultEnvelopeContractMapper.ToDomain(req.MemberIndex);
        var memberSecret = VaultEnvelopeContractMapper.ToDomain(req.MemberSecret);
        var agentDiscovery = req.AgentDiscovery is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.AgentDiscovery);

        var existing = await domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.VaultId == req.VaultId
                                      && x.Id == req.EntryId, ct);
        if (existing is not null)
        {
            if (!existing.IsExactCreateRetry(entryKey, memberSecret, memberIndex, agentDiscovery))
            {
                ThrowError("Entry identifier is already bound to a different canonical transition.");
            }

            await Send.OkAsync(new CreateEntryResponse(existing.Id, existing.CurrentRevision.Value.ToString()), ct);
            return;
        }

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await domainWriteContext.LockVaultGrantIds(organizationId, req.VaultId).ToListAsync(ct);
        var activeFullGrants = await domainWriteContext.Grants.OfType<FullGrant>()
            .Include(g => g.GrantEntryScopes).ThenInclude(scope => scope.Envelope)
            .Where(
            g => g.OrganizationId == organizationId
                 && g.VaultId == req.VaultId
                 && g.Status == GrantStatus.Active)
            .ToListAsync(ct);

        var now = clock.GetCurrentInstant();
        var scope = new EntryScope(vault.OrganizationId, vault.Id, req.EntryId);
        var challenge = await domainWriteContext.EntryCreationChallenges
            .FirstOrDefaultAsync(x => x.OrganizationId == scope.OrganizationId
                                      && x.VaultId == scope.VaultId
                                      && x.EntryId == scope.EntryId, ct);
        if (challenge is null)
        {
            ThrowError("A valid Entry creation challenge is required.");
        }

        challenge.Consume(scope, userId, now);
        var sequences = vault.AllocateSequences(agentDiscovery is not null, userId, now);
        var version = VaultEntryVersion.Create(
            memberSecret,
            sequences,
            memberIndexChanged: true,
            now,
            Palladin.Core.Types.ActorType.Member,
            userId);
        var entry = VaultEntry.Create(
            scope,
            entryKey,
            version,
            memberIndex,
            agentDiscovery,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            now,
            userId);

        var agentIds = activeFullGrants.Select(g => g.AgentId).Distinct().ToArray();
        var agentKeys = await domainWriteContext.Agents
            .Where(a => a.OrganizationId == organizationId && agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.PublicKey, a.RecipientKeyVersion })
            .ToListAsync(ct);
        var agentFingerprints = agentKeys.ToDictionary(
            a => a.Id,
            a => VaultKeyFingerprint.Compute(Convert.FromBase64String(a.PublicKey), VaultKeyKind.AgentX25519));
        if (!FullGrantEntryWrapper.TryWrapNewEntry(
                activeFullGrants,
                scope,
                req.GrantEnvelopes,
                agentFingerprints,
                agentKeys.ToDictionary(a => a.Id, a => a.RecipientKeyVersion),
                vault.MemberKeyGeneration.Value,
                out var grantError))
        {
            AddError(r => r.GrantEnvelopes, grantError);
            await Send.ErrorsAsync(409, ct);
            return;
        }

        domainWriteContext.Add(entry);
        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.CreatedAtAsync<GetEntryEndpoint>(
            new { vaultId = req.VaultId, entryId = entry.Id },
            new CreateEntryResponse(entry.Id, entry.CurrentRevision.Value.ToString()),
            cancellation: ct);
    }
}
