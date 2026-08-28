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
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CreateGrantRequest : IRequiresVaultMembership
{
    public Guid GrantId { get; init; }
    public Guid VaultId { get; init; }
    public Guid AgentId { get; init; }
    public GrantType Type { get; init; }
    public Guid? EntryId { get; init; }
    public Guid? ScriptEntryId { get; init; }
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEntries { get; init; } = [];
    public AgentWrappedVaultKeyContract? AgentWrappedVaultKey { get; init; }
    public ScriptExecutionPackageContract? ScriptPackage { get; init; }
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }

    // Methods the grant whitelists. Defaults to Get for clients that predate method selection.
    public GrantMethods Methods { get; init; } = GrantMethods.Get;
}

[PublicAPI]
public sealed record CreateGrantResponse(Guid Id);

[UsedImplicitly]
internal sealed class CreateGrantValidator : Validator<CreateGrantRequest>
{
    public CreateGrantValidator(IOptions<VaultCryptoOptions> cryptoOptions)
    {
        var crypto = cryptoOptions.Value;
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Methods).Must(m => m.IsValidSet());

        // At most one expiry policy: time-based (ExpiresAt), use-based (QueryLimit), or neither
        // (Lifetime = never expires). Both at once is invalid.
        RuleFor(x => x).Must(x => !(x.ExpiresAt.HasValue && x.QueryLimit.HasValue));
        RuleFor(x => x.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(x => x.QueryLimit!.Value).GreaterThan(0).When(x => x.QueryLimit.HasValue);

        RuleFor(x => x.EntryId).NotNull().When(x => x.Type == GrantType.Granular);
        RuleFor(x => x.EntryId).Null().When(x => x.Type != GrantType.Granular);
        RuleFor(x => x.ScriptEntryId).NotNull().When(x => x.Type == GrantType.ScriptExecution);
        RuleFor(x => x.ScriptEntryId).Null().When(x => x.Type != GrantType.ScriptExecution);

        RuleFor(x => x.GrantEntries).NotEmpty().When(x => x.Type == GrantType.Granular);
        RuleFor(x => x.GrantEntries).Empty().When(x => x.Type != GrantType.Granular);
        RuleFor(x => x.AgentWrappedVaultKey).Null().When(x => x.Type == GrantType.Granular);
        RuleFor(x => x.AgentWrappedVaultKey).Null().When(x => x.Type == GrantType.ScriptExecution);
        RuleFor(x => x.AgentWrappedVaultKey).NotNull()
            .SetValidator(new AgentWrappedVaultKeyContractValidator()!)
            .When(x => x.Type == GrantType.Full);
        RuleFor(x => x.ScriptPackage).Null().When(x => x.Type != GrantType.ScriptExecution);
        RuleFor(x => x.ScriptPackage).NotNull()
            .SetValidator(new ScriptExecutionPackageContractValidator()!)
            .When(x => x.Type == GrantType.ScriptExecution);
        RuleFor(x => x.Methods).Equal(GrantMethods.Exec)
            .When(x => x.Type == GrantType.ScriptExecution);
        RuleFor(x => x.GrantEntries.Count)
            .LessThanOrEqualTo(crypto.MaxGrantEntriesPerGrant);
        RuleFor(x => x.GrantEntries.Count)
            .Equal(1)
            .When(x => x.Type == GrantType.Granular);
        // Reject duplicate EntryIds explicitly. Silent dedup would let FULL grants build two
        // GrantEntry rows for the same (GrantId, EntryId) and hit a unique-key violation at
        // SaveChanges with no useful error to the client.
        RuleFor(x => x.GrantEntries)
            .Must(es => es.Select(e => e.EntryId).Distinct().Count() == es.Count);
        RuleFor(x => x)
            .Must(x => x.GrantEntries.Count == 1 && x.GrantEntries[0].EntryId == x.EntryId)
            .When(x => x.Type == GrantType.Granular && x.EntryId is not null);

        RuleForEach(x => x.GrantEntries).SetValidator(new GrantEntryEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class CreateGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateGrantRequest, CreateGrantResponse>
{
    private const int FullSupersedePageSize = 100;

    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a grant proactively (user-initiated)";
            summary.Description = "Creates an Active grant for an Agent. GRANULAR carries one revision-bound Entry envelope, FULL carries one current VK, and ScriptExecution carries one complete Script package sealed to the authoritative Agent key. The server stores ciphertext only.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CreateGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var agent = await domainWriteContext.Agents
            .Where(a => a.Id == req.AgentId && a.OrganizationId == organizationId)
            .FirstOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active
            || await domainReadContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == req.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError(r => r.AgentId, "Agent is not active.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (req.Type != GrantType.Full && !await EntriesBelongToVaultAsync(req, ct))
        {
            AddError(r => r.GrantEntries, "One or more entries do not belong to the vault.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == req.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError(r => r.AgentId, "Agent deactivation is in progress.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var lockedVault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var requestedEntryIds = req.Type switch
        {
            GrantType.Granular => req.GrantEntries.Select(x => x.EntryId),
            GrantType.ScriptExecution => req.ScriptPackage!.Scopes.Select(x => x.EntryId),
            _ => [],
        };
        var lockedRevisions = new Dictionary<Guid, ulong>();
        var lockedDeliveryPolicies = new Dictionary<Guid, GrantDeliveryPolicy>();
        foreach (var entryId in requestedEntryIds.Distinct().Order())
        {
            var lockedEntry = await domainWriteContext.Entries.SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.VaultId == req.VaultId
                     && x.Id == entryId,
                ct);
            if (lockedEntry is null || lockedEntry.State != EntryState.Active)
            {
                AddError(r => r.GrantEntries, "One or more entries are no longer active.");
                await Send.ErrorsAsync(409, ct);
                return;
            }
            lockedRevisions.Add(entryId, lockedEntry.CurrentRevision.Value);
            lockedDeliveryPolicies.Add(entryId, lockedEntry.DeliveryPolicy);
        }

        // TODO: enforce an anti-scraping policy. An agent may access exactly one entry at a time
        // (discovery -> request -> deliver). Enforcement of rate/velocity limits (e.g. cap on
        // concurrent active grants per agent, throttling sequential requests) to prevent an agent
        // from exfiltrating secrets one-by-one belongs to the delivery path and is out of scope here.

        // Coverage invariant: an agent has at most one active grant covering an entry. Reject if it
        // already has active coverage (409). For GRANULAR, a pending request for the same (agent, entry)
        // is resolved (approved) in place instead of creating a duplicate, so the pending list clears.
        var now = clock.GetCurrentInstant();
        var expirySource = ExpirySource.From(req.ExpiresAt, req.QueryLimit);

        if (req.Type == GrantType.Granular)
        {
            var entryId = req.EntryId!.Value;
            if (await domainReadContext.HasActiveEntryCoverageAsync(req.AgentId, agent.AccessEpoch, req.VaultId, entryId, excludingGrantId: null, ct))
            {
                throw new AgentAlreadyHasActiveAccessException("entry");
            }

            var pending = await domainWriteContext.FindPendingForEntryAsync(req.AgentId, agent.AccessEpoch, req.VaultId, entryId, ct);
            if (pending is not null)
            {
                if (req.GrantId != pending.Id)
                {
                    AddError(r => r.GrantId, "Grant envelope must target the existing pending request.");
                    await Send.ErrorsAsync(409, ct);
                    return;
                }
                var pendingNames = await domainReadContext.ResolveAsync(req.AgentId, entryId, req.VaultId, userId, ct);
                GrantEntryScope pendingScope;
                try
                {
                    pendingScope = ToScope(
                        req.GrantEntries.Single(), req, organizationId, agent.PublicKey,
                        lockedVault.MemberKeyGeneration.Value, agent.RecipientKeyVersion, lockedRevisions);
                }
                catch (Exception ex) when (ex is FormatException
                    or Palladin.Core.Types.Exceptions.DomainException
                    or StaleGrantEnvelopeException)
                {
                    AddError(r => r.GrantEntries, "Grant envelope is invalid or stale.");
                    await Send.ErrorsAsync(409, ct);
                    return;
                }
                pending.Approve(userId, pendingNames, pendingScope, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods, now);
                agent.FenceAccessMutation();
                lockedVault.FenceAccessMutation(userId, now);
                await domainWriteContext.CommitAsync(ct);

                await Send.CreatedAtAsync<GetGrantEndpoint>(
                    new { vaultId = req.VaultId, grantId = pending.Id },
                    new CreateGrantResponse(pending.Id),
                    cancellation: ct);
                return;
            }

            if (await domainReadContext.Grants.AnyAsync(g => g.Id == req.GrantId, ct))
            {
                AddError(r => r.GrantId, "Grant identifier has already been used.");
                await Send.ErrorsAsync(409, ct);
                return;
            }
        }
        else if (req.Type == GrantType.Full)
        {
            // Creating a FULL grant. 409 only on a real duplicate (an existing active FULL on the vault).
            if (await domainReadContext.HasActiveFullCoverageAsync(req.AgentId, agent.AccessEpoch, req.VaultId, ct))
            {
                throw new AgentAlreadyHasActiveAccessException("vault");
            }

            if (await domainReadContext.Grants.AnyAsync(g => g.Id == req.GrantId, ct))
            {
                AddError(r => r.GrantId, "Grant identifier has already been used.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

        }
        else
        {
            var scriptEntryId = req.ScriptEntryId!.Value;
            if (lockedDeliveryPolicies[scriptEntryId] != GrantDeliveryPolicy.ExecOnly)
            {
                AddError(r => r.ScriptEntryId, "Script Entry must use the exec-only delivery policy.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            var coveringFull = await domainReadContext.Grants
                .OfType<FullGrant>()
                .AnyAsync(grant => grant.AgentId == req.AgentId
                    && grant.AgentAccessEpoch == agent.AccessEpoch
                    && grant.VaultId == req.VaultId
                    && grant.Status == GrantStatus.Active
                    && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec
                    && grant.AgentWrappedVaultKey != null, ct);
            var duplicateScriptGrant = await domainReadContext.Grants
                .OfType<ScriptExecutionGrant>()
                .AnyAsync(grant => grant.AgentId == req.AgentId
                    && grant.AgentAccessEpoch == agent.AccessEpoch
                    && grant.VaultId == req.VaultId
                    && grant.ScriptEntryId == scriptEntryId
                    && grant.Status == GrantStatus.Active
                    && grant.ScriptExecutionPackage != null, ct);
            if (coveringFull || duplicateScriptGrant)
            {
                throw new AgentAlreadyHasActiveAccessException("script");
            }

            if (await domainReadContext.Grants.AnyAsync(g => g.Id == req.GrantId, ct))
            {
                AddError(r => r.GrantId, "Grant identifier has already been used.");
                await Send.ErrorsAsync(409, ct);
                return;
            }
        }

        // Resolve denormalized names for the GrantCreated event in one round-trip — audit/analytics
        // consumers render "who created what" without a re-resolve hop. EntryId is null for FULL
        // (leaves EntryLabel null); userId is the creator.
        var nameEntryId = req.Type switch
        {
            GrantType.Granular => req.EntryId,
            GrantType.ScriptExecution => req.ScriptEntryId,
            _ => null,
        };
        var names = await domainReadContext.ResolveAsync(req.AgentId, nameEntryId, req.VaultId, userId, ct);

        var scopes = new List<GrantEntryScope>(req.GrantEntries.Count);
        AgentWrappedVaultKey? agentWrappedVaultKey = null;
        ScriptExecutionPackage? scriptExecutionPackage = null;
        IReadOnlyList<ScriptExecutionScope> scriptExecutionScopes = [];
        try
        {
            foreach (var contract in req.GrantEntries)
            {
                scopes.Add(ToScope(
                    contract, req, organizationId, agent.PublicKey,
                    lockedVault.MemberKeyGeneration.Value, agent.RecipientKeyVersion, lockedRevisions));
            }

            if (req.Type == GrantType.Full)
            {
                var fingerprint = VaultKeyFingerprint.Compute(
                    Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
                agentWrappedVaultKey = AgentWrappedVaultKeyContractMapper.ToDomain(
                    req.AgentWrappedVaultKey!, organizationId, req.VaultId, req.GrantId, req.AgentId,
                    agent.AccessEpoch, lockedVault.CurrentVaultKeyVersion.Value,
                    agent.RecipientKeyVersion, fingerprint,
                    lockedVault.CurrentManifestSigningKeyVersion.Value,
                    lockedVault.ManifestSigningKeyFingerprint,
                    lockedVault.ManifestSigningPublicKey);
            }

            if (req.Type == GrantType.ScriptExecution)
            {
                (scriptExecutionPackage, scriptExecutionScopes) =
                    ScriptExecutionPackageContractMapper.ToDomain(req.ScriptPackage!);
                ScriptExecutionGrantMaterialValidator.Validate(
                    scriptExecutionPackage,
                    scriptExecutionScopes,
                    organizationId,
                    req.VaultId,
                    req.GrantId,
                    req.AgentId,
                    agent.AccessEpoch,
                    req.ScriptEntryId!.Value,
                    agent.PublicKey,
                    agent.RecipientKeyVersion,
                    lockedRevisions);
                ScriptExecutionPackageCryptoValidator.ValidateProducer(req.ScriptPackage!, lockedVault);
            }
        }
        catch (StaleGrantEnvelopeException)
        {
            AddCryptoError(req);
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddCryptoError(req);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var grant = BuildGrant(
            req,
            scopes,
            agentWrappedVaultKey,
            scriptExecutionPackage,
            scriptExecutionScopes,
            organizationId,
            agent.AccessEpoch,
            agent.PublicKey,
            userId,
            names,
            now,
            expirySource);

        // FULL promotion is a hard-atomic coverage transition. Persist superseded GRANULAR grants in
        // deterministic, bounded tracking pages inside one database transaction, but publish their
        // domain events only after the new FULL grant commits. Clearing EF tracking after every flush
        // keeps memory proportional to the page size even for a Vault with thousands of grants.
        await using var fullTransaction = req.Type == GrantType.Full
            ? await domainWriteContext.BeginTransactionAsync(ct)
            : null;
        if (fullTransaction is not null)
        {
            var expectedAccessEpoch = agent.AccessEpoch;
            var expectedPublicKey = agent.PublicKey;
            var expectedRecipientKeyVersion = agent.RecipientKeyVersion;
            var expectedVaultKeyVersion = lockedVault.CurrentVaultKeyVersion;
            var expectedMemberKeyGeneration = lockedVault.MemberKeyGeneration;

            var stableAgent = await domainWriteContext.LockAgent(organizationId, req.AgentId)
                .SingleAsync(ct);
            var stableVault = await domainWriteContext.LockVault(organizationId, req.VaultId)
                .SingleAsync(ct);
            if (stableAgent.Status != AgentStatus.Active
                || stableAgent.AccessEpoch != expectedAccessEpoch
                || stableAgent.PublicKey != expectedPublicKey
                || stableAgent.RecipientKeyVersion != expectedRecipientKeyVersion
                || stableVault.CurrentVaultKeyVersion != expectedVaultKeyVersion
                || stableVault.MemberKeyGeneration != expectedMemberKeyGeneration)
            {
                await fullTransaction.RollbackAsync(ct);
                AddError(r => r.AgentWrappedVaultKey, "Agent or Vault key context changed during grant creation.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            domainWriteContext.Clear();
            Guid? afterGrantId = null;
            while (true)
            {
                var page = await domainWriteContext.LoadActiveGranularInVaultPageAsync(
                    req.AgentId, expectedAccessEpoch, req.VaultId, afterGrantId, FullSupersedePageSize, ct);
                if (page.Count == 0)
                {
                    break;
                }

                domainWriteContext.EnsureFullGrantCommitTrackingIsBounded(FullSupersedePageSize);
                var entryIds = page.Select(g => g.EntryId).Distinct().ToArray();
                var namesByEntry = await domainReadContext.ResolveForSupersedeAsync(
                    req.AgentId, req.VaultId, entryIds, ct);
                foreach (var granular in page)
                {
                    granular.RevokeBySystem(namesByEntry[granular.EntryId], now);
                }

                afterGrantId = page[^1].Id;
                await domainWriteContext.FlushAsync(ct);
                domainWriteContext.Clear();
            }

            if (req.Methods.HasFlag(GrantMethods.Exec))
            {
                Guid? afterScriptGrantId = null;
                while (true)
                {
                    var page = await domainWriteContext.LoadActiveScriptExecutionsInVaultPageAsync(
                        req.AgentId, expectedAccessEpoch, req.VaultId, afterScriptGrantId,
                        FullSupersedePageSize, ct);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    domainWriteContext.EnsureFullGrantCommitTrackingIsBounded(FullSupersedePageSize);
                    var scriptEntryIds = page.Select(g => g.ScriptEntryId).Distinct().ToArray();
                    var namesByEntry = await domainReadContext.ResolveForSupersedeAsync(
                        req.AgentId, req.VaultId, scriptEntryIds, ct);
                    foreach (var scriptGrant in page)
                    {
                        scriptGrant.RevokeBySystem(namesByEntry[scriptGrant.ScriptEntryId], now);
                    }

                    afterScriptGrantId = page[^1].Id;
                    await domainWriteContext.FlushAsync(ct);
                    domainWriteContext.Clear();
                }
            }

            agent = await domainWriteContext.Agents.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == req.AgentId, ct);
            lockedVault = await domainWriteContext.Vaults.SingleAsync(
                x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        }

        domainWriteContext.Add(grant);
        agent.FenceAccessMutation();
        lockedVault.FenceAccessMutation(userId, now);
        try
        {
            if (fullTransaction is null)
            {
                await domainWriteContext.CommitAsync(ct);
            }
            else
            {
                await domainWriteContext.CommitAsync(fullTransaction, ct);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_Grants",
        })
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_AgentWrappedVaultKeys_OrganizationId_VaultId_AgentId",
        })
        {
            AddError(r => r.AgentId, "Agent already has active FULL access to this vault.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Grants_OrganizationId_VaultId_ScriptEntryId_AgentId_AgentAc~",
        })
        {
            AddError(r => r.AgentId, "Agent already has active direct access to this Script.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = grant.Id },
            new CreateGrantResponse(grant.Id),
            cancellation: ct);
    }

    private async Task<bool> EntriesBelongToVaultAsync(CreateGrantRequest req, CancellationToken ct)
    {
        var entryIds = (req.Type == GrantType.ScriptExecution
                ? req.ScriptPackage!.Scopes.Select(scope => scope.EntryId)
                : req.GrantEntries.Select(entry => entry.EntryId))
            .Distinct()
            .ToArray();
        var organizationId = User.GetOrganizationId()!.Value;
        var matching = await domainReadContext.Entries
            .CountAsync(e => e.OrganizationId == organizationId
                             && e.VaultId == req.VaultId
                             && e.State == EntryState.Active
                             && entryIds.Contains(e.Id), ct);
        return matching == entryIds.Length;
    }

    private void AddCryptoError(CreateGrantRequest req)
    {
        if (req.Type == GrantType.Full)
        {
            AddError(r => r.AgentWrappedVaultKey, "Agent Vault-key wrapper is invalid or stale.");
            return;
        }

        if (req.Type == GrantType.ScriptExecution)
        {
            AddError(r => r.ScriptPackage, "Script execution package is invalid or stale.");
            return;
        }

        AddError(r => r.GrantEntries, "Grant envelope is invalid or stale.");
    }

    private Grant BuildGrant(
        CreateGrantRequest req,
        IReadOnlyCollection<GrantEntryScope> scopes,
        AgentWrappedVaultKey? agentWrappedVaultKey,
        ScriptExecutionPackage? scriptExecutionPackage,
        IReadOnlyCollection<ScriptExecutionScope> scriptExecutionScopes,
        Guid organizationId,
        uint agentAccessEpoch,
        string agentPublicKey,
        Guid userId,
        GrantNames names,
        Instant now,
        string expirySource)
    {
        return req.Type switch
        {
            GrantType.Granular => GranularGrant.CreateProactively(
                req.GrantId,
                req.VaultId,
                organizationId,
                req.AgentId,
                agentPublicKey,
                req.EntryId!.Value,
                scopes.Single(),
                req.ExpiresAt,
                req.QueryLimit,
                expirySource,
                req.Methods,
                userId,
                names,
                now,
                agentAccessEpoch),
            GrantType.Full => FullGrant.CreateProactively(
                req.GrantId,
                req.VaultId,
                organizationId,
                req.AgentId,
                agentPublicKey,
                agentWrappedVaultKey!,
                req.ExpiresAt,
                req.QueryLimit,
                expirySource,
                req.Methods,
                userId,
                names,
                now,
                agentAccessEpoch),
            GrantType.ScriptExecution => ScriptExecutionGrant.CreateProactively(
                req.GrantId,
                req.VaultId,
                organizationId,
                req.AgentId,
                agentPublicKey,
                req.ScriptEntryId!.Value,
                scriptExecutionScopes,
                scriptExecutionPackage!,
                req.ExpiresAt,
                req.QueryLimit,
                expirySource,
                userId,
                names,
                now,
                agentAccessEpoch),
            _ => throw new ArgumentOutOfRangeException(nameof(req)),
        };
    }

    private static GrantEntryScope ToScope(
        GrantEntryEnvelopeContract contract,
        CreateGrantRequest req,
        Guid organizationId,
        string agentPublicKey,
        uint memberKeyGeneration,
        uint recipientAgentKeyVersion,
        IReadOnlyDictionary<Guid, ulong> lockedRevisions)
    {
        if (contract.OrganizationId != organizationId || contract.VaultId != req.VaultId
            || contract.GrantId != req.GrantId || contract.ExpiresAt != req.ExpiresAt
            || contract.RemainingUses != req.QueryLimit || contract.GrantEnvelopeRevision != "1"
            || contract.GrantKeyVersion != 1
            || contract.MemberKeyGeneration != memberKeyGeneration
            || contract.RecipientAgentKeyVersion != recipientAgentKeyVersion)
        {
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant envelope scope or lifecycle is invalid.");
        }
        var revision = lockedRevisions[contract.EntryId];
        if (contract.EntryRevision != revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new StaleGrantEnvelopeException();
        }
        var scope = GrantEnvelopeContractMapper.ToDomain(contract, req.Methods, req.AgentId);
        var fingerprint = VaultKeyFingerprint.Compute(Convert.FromBase64String(agentPublicKey), VaultKeyKind.AgentX25519);
        if (!scope.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant envelope Agent key fingerprint is invalid.");
        }
        return scope;
    }

    private sealed class StaleGrantEnvelopeException()
        : Palladin.Core.Types.Exceptions.ConflictException("Grant envelope entry revision is stale.");
}
