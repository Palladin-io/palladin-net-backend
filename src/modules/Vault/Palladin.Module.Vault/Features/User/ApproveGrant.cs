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
    public GrantEntryEnvelopeContract? GrantEntry { get; init; }
    public ScriptExecutionPackageContract? ScriptPackage { get; init; }
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

        RuleFor(x => x)
            .Must(x => (x.GrantEntry is null) != (x.ScriptPackage is null));
        RuleFor(x => x.GrantEntry)
            .SetValidator(new GrantEntryEnvelopeContractValidator()!)
            .When(x => x.GrantEntry is not null);
        RuleFor(x => x.ScriptPackage)
            .SetValidator(new ScriptExecutionPackageContractValidator()!)
            .When(x => x.ScriptPackage is not null);
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
            summary.Description = "Activates one pending Granular or ScriptExecution request. The approving client supplies either one per-entry envelope or one complete encrypted Script package. Sets at most one expiry policy; neither means lifetime access.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(ApproveGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var lockedVault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var grant = await domainWriteContext.Grants
            .Include(g => g.EncryptedReason)
            .FirstOrDefaultAsync(g => g.OrganizationId == organizationId
                                      && g.Id == req.GrantId
                                      && g.VaultId == req.VaultId
                                      && (g is GranularGrant || g is ScriptExecutionGrant), ct);
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

        var currentAgent = await domainWriteContext.Agents
            .Where(agent => agent.OrganizationId == grant.OrganizationId
                            && agent.Id == grant.AgentId
                            && agent.Status == AgentStatus.Active)
            .SingleOrDefaultAsync(ct);
        if (currentAgent is null || currentAgent.AccessEpoch != grant.AgentAccessEpoch)
        {
            AddError("Grant belongs to a previous Agent access epoch.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (grant is ScriptExecutionGrant scriptExecutionGrant)
        {
            await ApproveScriptExecutionAsync(
                req,
                scriptExecutionGrant,
                currentAgent,
                lockedVault,
                userId,
                organizationId,
                ct);
            return;
        }

        var granularGrant = (GranularGrant)grant;
        var grantEntry = req.GrantEntry!;
        if (grantEntry.OrganizationId != granularGrant.OrganizationId
            || grantEntry.VaultId != granularGrant.VaultId
            || grantEntry.GrantId != granularGrant.Id
            || grantEntry.EntryId != granularGrant.EntryId)
        {
            AddError(r => r.GrantEntry, "Crypto material does not match the granted entry.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Coverage invariant: refuse to activate if the agent already has active coverage of this entry
        // through another grant (active GRANULAR on the entry, or active FULL on the vault) — 409.
        if (await domainReadContext.HasActiveEntryCoverageAsync(
                granularGrant.AgentId,
                granularGrant.AgentAccessEpoch,
                granularGrant.VaultId,
                granularGrant.EntryId,
                excludingGrantId: granularGrant.Id,
                ct))
        {
            throw new AgentAlreadyHasActiveAccessException("entry");
        }

        var lockedEntry = await domainWriteContext.Entries.SingleOrDefaultAsync(
            x => x.OrganizationId == granularGrant.OrganizationId
                 && x.VaultId == granularGrant.VaultId
                 && x.Id == granularGrant.EntryId,
            ct);
        var finalMethods = req.Methods ?? granularGrant.Methods;
        if (lockedEntry is null
            || lockedEntry.State != EntryState.Active
            || grantEntry.EntryRevision != lockedEntry.CurrentRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || grantEntry.GrantEnvelopeRevision != "1"
            || grantEntry.GrantKeyVersion != 1
            || grantEntry.MemberKeyGeneration != lockedVault.MemberKeyGeneration.Value
            || grantEntry.RecipientAgentKeyVersion != currentAgent.RecipientKeyVersion
            || grantEntry.ExpiresAt != req.ExpiresAt
            || grantEntry.RemainingUses != req.QueryLimit)
        {
            AddError(r => r.GrantEntry, "Grant envelope revision or lifetime is stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        GrantEntryScope scope;
        try
        {
            scope = GrantEnvelopeContractMapper.ToDomain(grantEntry, finalMethods, granularGrant.AgentId);
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
            granularGrant.AgentId,
            granularGrant.EntryId,
            granularGrant.VaultId,
            userId,
            ct);
        var expirySource = ExpirySource.From(req.ExpiresAt, req.QueryLimit);
        var now = clock.GetCurrentInstant();
        granularGrant.Approve(
            userId,
            names,
            scope,
            req.ExpiresAt,
            req.QueryLimit,
            expirySource,
            req.Methods,
            now);

        currentAgent.FenceAccessMutation();
        lockedVault.FenceAccessMutation(userId, now);
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }

    private async Task ApproveScriptExecutionAsync(
        ApproveGrantRequest req,
        ScriptExecutionGrant grant,
        Agent currentAgent,
        Palladin.Module.Vault.Domain.Vault lockedVault,
        Guid userId,
        Guid organizationId,
        CancellationToken ct)
    {
        var finalMethods = req.Methods ?? grant.Methods;
        if (req.ScriptPackage is null
            || req.GrantEntry is not null
            || finalMethods != GrantMethods.Exec)
        {
            AddError(request => request.ScriptPackage,
                "A ScriptExecution approval requires one complete Script package and Exec only.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var hasActiveCoverage = await domainReadContext.Grants.AnyAsync(candidate =>
            candidate.Id != grant.Id
            && candidate.AgentId == grant.AgentId
            && candidate.AgentAccessEpoch == grant.AgentAccessEpoch
            && candidate.VaultId == grant.VaultId
            && candidate.Status == GrantStatus.Active
            && ((candidate is FullGrant
                 && (candidate.Methods & GrantMethods.Exec) == GrantMethods.Exec
                 && candidate.AgentWrappedVaultKey != null)
                || (candidate is ScriptExecutionGrant
                    && ((ScriptExecutionGrant)candidate).ScriptEntryId == grant.ScriptEntryId
                    && candidate.ScriptExecutionPackage != null)), ct);
        if (hasActiveCoverage)
        {
            throw new AgentAlreadyHasActiveAccessException("script");
        }

        var lockedRevisions = new Dictionary<Guid, ulong>();
        var scriptDeliveryPolicy = GrantDeliveryPolicy.Standard;
        foreach (var entryId in req.ScriptPackage.Scopes
                     .Select(scope => scope.EntryId)
                     .Distinct()
                     .Order())
        {
            var entry = await domainWriteContext.Entries.SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId
                             && candidate.VaultId == grant.VaultId
                             && candidate.Id == entryId,
                ct);
            if (entry is null || entry.State != EntryState.Active)
            {
                AddError(request => request.ScriptPackage,
                    "One or more Script package entries are no longer active.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            lockedRevisions.Add(entryId, entry.CurrentRevision.Value);
            if (entryId == grant.ScriptEntryId)
            {
                scriptDeliveryPolicy = entry.DeliveryPolicy;
            }
        }

        if (scriptDeliveryPolicy != GrantDeliveryPolicy.ExecOnly)
        {
            AddError(request => request.ScriptPackage,
                "Script Entry must use the exec-only delivery policy.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        ScriptExecutionPackage package;
        IReadOnlyList<ScriptExecutionScope> scopes;
        try
        {
            (package, scopes) = ScriptExecutionPackageContractMapper.ToDomain(req.ScriptPackage);
            ScriptExecutionGrantMaterialValidator.Validate(
                package,
                scopes,
                organizationId,
                grant.VaultId,
                grant.Id,
                grant.AgentId,
                grant.AgentAccessEpoch,
                grant.ScriptEntryId,
                currentAgent.PublicKey,
                currentAgent.RecipientKeyVersion,
                lockedRevisions);
            ScriptExecutionPackageCryptoValidator.ValidateProducer(req.ScriptPackage, lockedVault);
        }
        catch (Exception ex) when (ex is FormatException
                                   or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(request => request.ScriptPackage,
                "Script execution package is invalid or stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var names = await domainReadContext.ResolveAsync(
            grant.AgentId,
            grant.ScriptEntryId,
            grant.VaultId,
            userId,
            ct);
        var now = clock.GetCurrentInstant();
        grant.Approve(
            userId,
            names,
            package,
            scopes,
            req.ExpiresAt,
            req.QueryLimit,
            ExpirySource.From(req.ExpiresAt, req.QueryLimit),
            now);
        currentAgent.FenceAccessMutation();
        lockedVault.FenceAccessMutation(userId, now);
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
