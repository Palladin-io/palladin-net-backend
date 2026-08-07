using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ApproveGrantRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
    public GrantEntryEnvelopeContract GrantEntry { get; init; } = null!;
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }

    // Final methods decision. The approver may narrow or widen what the agent requested;
    // null keeps the requested set unchanged.
    public GrantMethods? Methods { get; init; }
}

[UsedImplicitly]
internal sealed class ApproveGrantValidator : Validator<ApproveGrantRequest>
{
    public ApproveGrantValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();

        // At most one expiry policy: time-based (ExpiresAt), use-based (QueryLimit), or neither
        // (Lifetime = never expires). Both at once is invalid.
        RuleFor(x => x).Must(x => !(x.ExpiresAt.HasValue && x.QueryLimit.HasValue));
        RuleFor(x => x.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(x => x.QueryLimit!.Value).GreaterThan(0).When(x => x.QueryLimit.HasValue);
        RuleFor(x => x.Methods!.Value).Must(m => m.IsValidSet()).When(x => x.Methods.HasValue);

        RuleFor(x => x.GrantEntry).NotNull();
        RuleFor(x => x.GrantEntry).SetValidator(new GrantEntryEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class ApproveGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproveGrantRequest>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/grants/{grantId:guid}/approve");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Approve a pending grant";
            summary.Description = "Activates a pending agent access request. The approving user's client supplies the per-entry re-wrapped DEK (ciphertext only — the server never wraps VK). Sets at most one expiry policy (expiresAt or queryLimit); neither means lifetime access.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(ApproveGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(organizationId).SingleAsync(ct);

        var lockedVault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await domainWriteContext.LockVaultGrantIds(organizationId, req.VaultId).ToListAsync(ct);

        var grant = await domainWriteContext.Grants
            .OfType<GranularGrant>()
            .Include(g => g.EncryptedReason)
            .FirstOrDefaultAsync(g => g.OrganizationId == organizationId
                                      && g.Id == req.GrantId
                                      && g.VaultId == req.VaultId, ct);
        if (grant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == grant.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError("Agent deactivation is in progress.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (req.GrantEntry.OrganizationId != grant.OrganizationId
            || req.GrantEntry.VaultId != grant.VaultId
            || req.GrantEntry.GrantId != grant.Id
            || req.GrantEntry.EntryId != grant.EntryId)
        {
            AddError(r => r.GrantEntry, "Crypto material does not match the granted entry.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var currentAgent = await domainReadContext.Agents
            .Where(agent => agent.OrganizationId == grant.OrganizationId
                            && agent.Id == grant.AgentId
                            && agent.Status == AgentStatus.Active)
            .Select(agent => new { agent.AccessEpoch, agent.PublicKey, agent.RecipientKeyVersion })
            .SingleOrDefaultAsync(ct);
        if (currentAgent is null || currentAgent.AccessEpoch != grant.AgentAccessEpoch)
        {
            AddError("Grant belongs to a previous Agent access epoch.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        // Coverage invariant: refuse to activate if the agent already has active coverage of this entry
        // through another grant (active GRANULAR on the entry, or active FULL on the vault) — 409.
        if (await domainReadContext.HasActiveEntryCoverageAsync(
                grant.AgentId, grant.AgentAccessEpoch, grant.VaultId, grant.EntryId, excludingGrantId: grant.Id, ct))
        {
            throw new AgentAlreadyHasActiveAccessException("entry");
        }

        var lockedEntry = await domainWriteContext.LockEntry(
                grant.OrganizationId, grant.VaultId, grant.EntryId)
            .SingleOrDefaultAsync(ct);
        var finalMethods = req.Methods ?? grant.Methods;
        if (lockedEntry is null
            || lockedEntry.State != EntryState.Active
            || req.GrantEntry.EntryRevision != lockedEntry.CurrentRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || req.GrantEntry.GrantEnvelopeRevision != "1"
            || req.GrantEntry.GrantKeyVersion != 1
            || req.GrantEntry.MemberKeyGeneration != lockedVault.MemberKeyGeneration.Value
            || req.GrantEntry.RecipientAgentKeyVersion != currentAgent.RecipientKeyVersion
            || req.GrantEntry.ExpiresAt != req.ExpiresAt
            || req.GrantEntry.RemainingUses != req.QueryLimit)
        {
            AddError(r => r.GrantEntry, "Grant envelope revision or lifetime is stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        GrantEntryScope scope;
        try
        {
            scope = GrantEnvelopeContractMapper.ToDomain(req.GrantEntry, finalMethods, grant.AgentId);
            var expectedFingerprint = VaultKeyFingerprint.Compute(
                Convert.FromBase64String(currentAgent.PublicKey), VaultKeyKind.AgentX25519);
            if (!scope.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(expectedFingerprint))
            {
                throw new Palladin.Core.Types.Exceptions.DomainException("Agent key fingerprint mismatch.");
            }
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(r => r.GrantEntry, "Grant envelope is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var names = await domainReadContext.ResolveAsync(
            grant.AgentId, grant.EntryId, grant.VaultId, userId, ct);
        var expirySource = ExpirySource.From(req.ExpiresAt, req.QueryLimit);
        grant.Approve(
            userId,
            names,
            scope,
            req.ExpiresAt,
            req.QueryLimit,
            expirySource,
            req.Methods,
            clock.GetCurrentInstant());

        await domainWriteContext.CommitAsync(transaction, ct);

        await Send.NoContentAsync(ct);
    }
}
