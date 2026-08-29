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
    public required MemberIndexEnvelopeContract MemberIndex { get; init; }
    public bool AgentDiscoveryChanged { get; init; }
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public GrantDeliveryPolicy DeliveryPolicy { get; init; } = GrantDeliveryPolicy.Standard;
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEnvelopes { get; init; } = [];
    public IReadOnlyList<ScriptExecutionPackageContract> ScriptGrantPackages { get; init; } = [];
    public IReadOnlyList<Guid> RevokedScriptGrantIds { get; init; } = [];
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
        RuleFor(x => x.MemberIndex).NotNull();
        RuleFor(x => x.DeliveryPolicy).Must(x => x.IsValid());
        RuleFor(x => x.AgentDiscovery).Null().When(x => !x.AgentDiscoveryChanged);
        RuleForEach(x => x.GrantEnvelopes).SetValidator(new GrantEntryEnvelopeContractValidator());
        RuleForEach(x => x.ScriptGrantPackages)
            .SetValidator(new ScriptExecutionPackageContractValidator());
        RuleFor(x => x.RevokedScriptGrantIds)
            .Must(ids => ids.Count <= 1000 && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Revoked Script grant ids must be non-empty, unique, and within the request limit.");
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
        var memberIndex = VaultEnvelopeContractMapper.ToDomain(req.MemberIndex);
        var agentDiscovery = req.AgentDiscovery is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.AgentDiscovery);
        var newKey = req.NewEntryKey is null
            ? null
            : VaultEnvelopeContractMapper.ToDomain(req.NewEntryKey);

        var vault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var entry = await domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.VaultId == req.VaultId
                                      && x.Id == req.EntryId, ct);
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
                agentDiscovery,
                req.DeliveryPolicy))
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
                 && g is GranularGrant
                 && g.GrantEntryScopes.Any(scope => scope.EntryId == req.EntryId
                                                    && scope.Envelope != null))
            .ToListAsync(ct);
        var activeScriptGrants = await domainWriteContext.Grants
            .OfType<ScriptExecutionGrant>()
            .Include(grant => grant.ScriptExecutionPackage)
            .Include(grant => grant.ScriptExecutionScopes)
            .Where(grant => grant.OrganizationId == organizationId
                && grant.VaultId == req.VaultId
                && grant.Status == GrantStatus.Active
                && grant.ScriptExecutionScopes.Any(scope => scope.EntryId == req.EntryId))
            .ToListAsync(ct);
        var coveringFullExecExists = (entry.DeliveryPolicy == GrantDeliveryPolicy.ExecOnly
                || req.DeliveryPolicy == GrantDeliveryPolicy.ExecOnly)
            && await domainWriteContext.Grants
                .OfType<FullGrant>()
                .AnyAsync(grant => grant.OrganizationId == organizationId
                    && grant.VaultId == req.VaultId
                    && grant.Status == GrantStatus.Active
                    && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec
                    && grant.AgentWrappedVaultKey != null, ct);
        if ((activeScriptGrants.Count > 0 || coveringFullExecExists)
            && !User.GetPermissions().HasFlag(Permission.GrantManage))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
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
        var revokedScriptGrants = req.DeliveryPolicy == GrantDeliveryPolicy.ExecOnly
            ? []
            : activeScriptGrants.Where(grant => grant.ScriptEntryId == req.EntryId).ToList();
        if (!revokedScriptGrants.Select(grant => grant.Id).Order().SequenceEqual(
                req.RevokedScriptGrantIds.Order()))
        {
            AddError(r => r.RevokedScriptGrantIds,
                "Every direct ScriptExecution grant invalidated by this Entry conversion must be revoked exactly once.");
            await Send.StatusCodeAsync(409, ct);
            return;
        }
        var refreshedScriptGrants = activeScriptGrants
            .Except(revokedScriptGrants)
            .ToList();
        if (!refreshedScriptGrants.Select(grant => grant.Id).Order().SequenceEqual(
                req.ScriptGrantPackages.Select(package => package.GrantId).Order()))
        {
            AddError(r => r.ScriptGrantPackages,
                "Every affected ScriptExecution grant must be refreshed exactly once.");
            await Send.StatusCodeAsync(409, ct);
            return;
        }

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
            req.DeliveryPolicy,
            sequences,
            now,
            userId);

        var affectedAgentIds = activeGrants.Select(grant => grant.AgentId)
            .Concat(activeScriptGrants.Select(grant => grant.AgentId))
            .Distinct()
            .ToArray();
        var agents = await domainWriteContext.Agents
            .Where(agent => affectedAgentIds.Contains(agent.Id)
                            && agent.OrganizationId == organizationId)
            .ToDictionaryAsync(agent => agent.Id, ct);
        try
        {
            foreach (var grant in revokedScriptGrants)
            {
                grant.RevokeBySystem(
                    new GrantNames(
                        agents.GetValueOrDefault(grant.AgentId)?.Name ?? GrantNames.UnknownAgent,
                        GrantNames.UnknownEntry,
                        string.Empty,
                        GrantNames.SystemActor),
                    now);
            }

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
                if (refreshed.DeliveryPolicy != req.DeliveryPolicy)
                {
                    throw new Palladin.Core.Types.Exceptions.DomainException("Grant refresh delivery policy does not match the Entry.");
                }
                var fingerprint = VaultKeyFingerprint.Compute(
                    Convert.FromBase64String(agents[grant.AgentId].PublicKey), VaultKeyKind.AgentX25519);
                if (!refreshed.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
                {
                    throw new Palladin.Core.Types.Exceptions.DomainException("Grant refresh Agent key is invalid.");
                }
                scope.RefreshScope(refreshed);
            }

            if (refreshedScriptGrants.Count > 0)
            {
                var packageScopeIds = req.ScriptGrantPackages
                    .SelectMany(package => package.Scopes)
                    .Select(scope => scope.EntryId)
                    .Distinct()
                    .ToArray();
                var currentPackageEntries = await domainWriteContext.Entries
                    .Where(candidate => candidate.OrganizationId == organizationId
                        && candidate.VaultId == req.VaultId
                        && candidate.State == EntryState.Active
                        && packageScopeIds.Contains(candidate.Id))
                    .Select(candidate => new { candidate.Id, Revision = candidate.CurrentRevision.Value })
                    .ToListAsync(ct);
                var currentRevisions = currentPackageEntries
                    .ToDictionary(candidate => candidate.Id, candidate => candidate.Revision);
                currentRevisions[entry.Id] = entry.CurrentRevision.Value;

                foreach (var grant in refreshedScriptGrants)
                {
                    var contract = req.ScriptGrantPackages.Single(package => package.GrantId == grant.Id);
                    var (replacement, replacementScopes) =
                        ScriptExecutionPackageContractMapper.ToDomain(contract);
                    var grantAgent = agents[grant.AgentId];
                    var fingerprint = VaultKeyFingerprint.Compute(
                        Convert.FromBase64String(grantAgent.PublicKey), VaultKeyKind.AgentX25519);
                    if (replacement.OrganizationId != organizationId
                        || replacement.VaultId != req.VaultId
                        || replacement.GrantId != grant.Id
                        || replacement.AgentId != grant.AgentId
                        || replacement.AgentAccessEpoch != grant.AgentAccessEpoch
                        || replacement.ScriptEntryId != grant.ScriptEntryId
                        || replacement.RecipientAgentKeyVersion != grantAgent.RecipientKeyVersion
                        || !replacement.RecipientAgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint)
                        || replacementScopes.Any(scope =>
                            !currentRevisions.TryGetValue(scope.EntryId, out var revision)
                            || scope.EntryRevision != revision)
                        || !currentRevisions.TryGetValue(grant.ScriptEntryId, out var scriptRevision)
                        || replacement.ScriptRevision != scriptRevision)
                    {
                        throw new Palladin.Core.Types.Exceptions.DomainException(
                            "Script execution package refresh scope is invalid.");
                    }

                    if (grant.ScriptEntryId != req.EntryId
                        && !grant.ScriptExecutionScopes.Select(scope => scope.EntryId).Order()
                            .SequenceEqual(replacementScopes.Select(scope => scope.EntryId).Order()))
                    {
                        throw new Palladin.Core.Types.Exceptions.DomainException(
                            "A referenced Entry update cannot change the Script scope set.");
                    }
                    ScriptExecutionPackageCryptoValidator.ValidateProducer(contract, vault);
                    grant.RefreshPackage(replacement, replacementScopes);
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError("Grant or Script execution package refresh is invalid or stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await domainWriteContext.CommitAsync(ct);
        await Send.OkAsync(new UpdateEntryResponse(entry.CurrentRevision.Value.ToString()), ct);
    }
}
