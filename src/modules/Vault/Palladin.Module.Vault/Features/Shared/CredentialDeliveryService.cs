using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

// Machine-readable denial reasons emitted on CredentialAccessDeniedEvent (audit + analytics). Stable
// strings — clients/triggers branch on them. Shared by DeliverCredential and GetOrRequestCredential
// so both delivery entry points produce the same audit trail.
internal static class CredentialDenialReasons
{
    public const string NoActiveGrant = "no_active_grant";
    public const string Expired = "expired";
    public const string MaterialUnavailable = "material_unavailable";
    public const string QueryLimit = "query_limit";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string ScriptExecOnly = "script_exec_only";
    public const string CreditCardInjectOnly = "credit_card_inject_only";
}

internal sealed record CredentialDeliveryInput(
    Guid GrantId,
    Guid OrganizationId,
    Guid AgentId,
    Guid VaultId,
    Guid EntryId,
    uint AgentAccessEpoch,
    Instant? ExpiresAt,
    int? QueryLimit,
    GrantType Type,
    GrantMethods AllowedMethods,
    GrantMethods Method,
    string? Ip,
    string? Hostname,
    Instant Now);

internal abstract record CredentialDeliveryResult
{
    public sealed record Granted(
        ulong GrantEnvelopeRevision,
        ulong EntryRevision,
        ushort ProtocolVersion,
        string CryptoSuiteId,
        uint GrantKeyVersion,
        uint MemberKeyGeneration,
        uint RecipientAgentKeyVersion,
        GrantDeliveryPolicy DeliveryPolicy,
        string[] FieldIds,
        byte[] EncodedSuitePayload,
        byte[] AgentWrappedGrantDek,
        string WrapperSuiteId,
        byte[] AgentKeyFingerprint,
        Instant? EnvelopeExpiresAt,
        int? EnvelopeRemainingUses,
        AgentWrappedVaultKeyContract? AgentWrappedVaultKey,
        VaultEntryKeyContract? EntryKey,
        MemberSecretEnvelopeContract? MemberSecret,
        int? RemainingUses,
        bool Consumed,
        string AgentName,
        string VaultName) : CredentialDeliveryResult;

    public sealed record Denied(string Reason) : CredentialDeliveryResult;
}

// Shared delivery path used by DeliverCredentialEndpoint (legacy GET) and
// GetOrRequestCredentialEndpoint (unified POST). Runs the expiry guard, reads the per-agent crypto
// material, performs the atomic race-safe check-and-increment / Consumed flip, and resolves the
// denormalized names carried on CredentialAccessedEvent. Returns a discriminated result so the
// caller owns the HTTP shape and the post-response publish.
[UsedImplicitly]
internal sealed class CredentialDeliveryService(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext)
{
    public async Task<CredentialDeliveryResult> ExecuteAsync(
        CredentialDeliveryInput input,
        CancellationToken ct)
    {
        if (input.ExpiresAt is not null && input.ExpiresAt.Value <= input.Now)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.Expired);
        }

        // Validate the complete tenant scope before reading grant material or incrementing a use.
        // Entry metadata is encrypted client-side and is never interpreted by this service.
        var activeEntry = await domainReadContext.Entries
            .Where(e => e.OrganizationId == input.OrganizationId
                        && e.VaultId == input.VaultId
                        && e.Id == input.EntryId
                        && e.State == EntryState.Active)
            .Select(e => new { e.CurrentRevision, e.CurrentKeyVersion, e.DeliveryPolicy })
            .FirstOrDefaultAsync(ct);
        if (activeEntry is null)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
        }

        var material = input.Type == GrantType.Granular
            ? await domainReadContext.GrantEntryScopes
                .Where(scope => scope.OrganizationId == input.OrganizationId
                            && scope.VaultId == input.VaultId
                            && scope.GrantId == input.GrantId
                            && scope.EntryId == input.EntryId
                            && scope.Envelope != null
                            && scope.Envelope.EntryRevision == activeEntry.CurrentRevision.Value)
            .Select(scope => new
            {
                FieldIds = scope.FieldIds,
                scope.DeliveryPolicy,
                envelope = scope.Envelope!,
            })
            .Select(scope => new
            {
                scope.FieldIds,
                scope.DeliveryPolicy,
                scope.envelope.GrantEnvelopeRevision,
                scope.envelope.EntryRevision,
                scope.envelope.ProtocolVersion,
                scope.envelope.CryptoSuiteId,
                scope.envelope.GrantKeyVersion,
                MemberKeyGeneration = scope.envelope.MemberKeyGeneration.Value,
                RecipientAgentKeyVersion = scope.envelope.RecipientAgentKeyVersion.Value,
                scope.envelope.EncodedSuitePayload,
                scope.envelope.AgentWrappedGrantDek,
                scope.envelope.WrapperSuiteId,
                scope.envelope.AgentKeyFingerprint,
                scope.envelope.ExpiresAt,
                scope.envelope.RemainingUses,
            })
                .FirstOrDefaultAsync(ct)
            : null;

        AgentWrappedVaultKeyContract? agentWrappedVaultKey = null;
        VaultEntryKeyContract? entryKey = null;
        MemberSecretEnvelopeContract? memberSecret = null;
        VaultKeyVersion? fullMaterialVaultKeyVersion = null;
        EntryKeyWrapperRevision? fullMaterialEntryKeyWrapperRevision = null;
        if (input.Type == GrantType.Full)
        {
            var fullWrap = await domainReadContext.AgentWrappedVaultKeys
                .SingleOrDefaultAsync(x => x.OrganizationId == input.OrganizationId
                    && x.VaultId == input.VaultId
                    && x.GrantId == input.GrantId
                    && x.AgentId == input.AgentId
                    && x.AgentAccessEpoch == input.AgentAccessEpoch, ct);
            var currentEntryKey = await domainReadContext.EntryKeys
                .SingleOrDefaultAsync(x => x.OrganizationId == input.OrganizationId
                    && x.VaultId == input.VaultId
                    && x.EntryId == input.EntryId
                    && x.KeyVersion == activeEntry.CurrentKeyVersion, ct);
            var currentVersion = await domainReadContext.EntryVersions
                .SingleOrDefaultAsync(x => x.OrganizationId == input.OrganizationId
                    && x.VaultId == input.VaultId
                    && x.EntryId == input.EntryId
                    && x.Revision == activeEntry.CurrentRevision, ct);

            if (fullWrap is not null
                && currentEntryKey is not null
                && currentVersion is not null
                && fullWrap.VaultKeyVersion == currentEntryKey.WrappingKeyVersion)
            {
                fullMaterialVaultKeyVersion = fullWrap.VaultKeyVersion;
                fullMaterialEntryKeyWrapperRevision = currentEntryKey.WrapperRevision;
                agentWrappedVaultKey = AgentWrappedVaultKeyContractMapper.ToContract(fullWrap);
                entryKey = VaultEnvelopeContractMapper.ToContract(currentEntryKey);
                memberSecret = VaultEnvelopeContractMapper.ToContract(currentVersion.GetMemberSecret());
            }
        }

        // FULL delivery is available only when the live grant wrapper targets the same Vault Key
        // generation that protects the Entry DEK. A mismatch is a fail-closed rotation state.
        if ((input.Type == GrantType.Granular && material is null)
            || (input.Type == GrantType.Full
                && (agentWrappedVaultKey is null || entryKey is null || memberSecret is null)))
        {
            if (input.QueryLimit is not null
                && await domainReadContext.Grants.AnyAsync(grant =>
                    grant.Id == input.GrantId
                    && grant.OrganizationId == input.OrganizationId
                    && grant.AgentId == input.AgentId
                    && grant.VaultId == input.VaultId
                    && grant.AgentAccessEpoch == input.AgentAccessEpoch
                    && grant.Status == GrantStatus.Consumed
                    && grant.QueryCount >= grant.QueryLimit,
                    ct))
            {
                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.QueryLimit);
            }
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
        }

        // DeliveryPolicy is authenticated by the grant descriptor and immutable on the durable
        // scope. Field identifiers are never treated as an Entry-type discriminator.
        var deliveryPolicy = input.Type == GrantType.Full
            ? activeEntry.DeliveryPolicy
            : material!.DeliveryPolicy;
        if (input.Method != GrantMethods.Exec
            && deliveryPolicy == GrantDeliveryPolicy.ExecOnly)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.ScriptExecOnly);
        }

        if (input.Method != GrantMethods.Inject
            && deliveryPolicy == GrantDeliveryPolicy.InjectOnly)
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.CreditCardInjectOnly);
        }

        // DeliveryPolicy classifies the protected resource before the independent method
        // whitelist. This preserves the policy-specific denial reason even for least-privilege
        // grants that whitelist only their one valid method. Neither denial path consumes a use.
        if (!input.AllowedMethods.HasFlag(input.Method))
        {
            return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MethodNotAllowed);
        }

        // BUG#2 fix: read denormalized names BEFORE the atomic increment / Consumed flip. A DB hiccup
        // on either FirstOrDefaultAsync after the increment would burn the use without delivering the
        // secret. Reading them upfront isolates the increment path from name-resolution failures.
        var agentName = await domainReadContext.Agents
            .Where(a => a.Id == input.AgentId && a.OrganizationId == input.OrganizationId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(ct);
        // Vault display metadata is encrypted and is never resolved server-side.
        var vaultName = string.Empty;

        int? remainingUses = null;
        var consumed = false;
        var materialEntryRevision = material?.EntryRevision ?? activeEntry.CurrentRevision.Value;
        var materialGrantEnvelopeRevision = material?.GrantEnvelopeRevision ?? 0UL;
        var fullMaterialVaultKeyVersionValue = fullMaterialVaultKeyVersion?.Value ?? 0U;
        var fullMaterialEntryKeyWrapperRevisionValue = fullMaterialEntryKeyWrapperRevision?.Value ?? 0UL;
        var expectedFullVaultKeyVersion = fullMaterialVaultKeyVersion ?? default;
        var expectedFullEntryKeyWrapperRevision = fullMaterialEntryKeyWrapperRevision ?? default;

        if (input.QueryLimit is not null)
        {
            // Atomic check-and-increment with RETURNING. DEVIATION (approved): raw conditional UPDATE
            // rather than the aggregate, because race-safe check-and-increment under concurrent
            // delivery requires the guard to run in the database. No row returned => limit already
            // reached (or grant revoked/expired concurrently) => 429. The last-access stamp (when / IP /
            // hostname) is written in the same statement so it never diverges from the counter.
            // BUG#1 fix: use the REAL post-increment QueryCount from RETURNING (not stale read+1), so
            // the Consumed transition and remainingUses are correct under concurrency.
            var newCount = await domainWriteContext.SqlQuery<int>(
                    $"""
                     WITH updated AS (
                         UPDATE "Grants"
                         SET "QueryCount" = "QueryCount" + 1,
                             "Status" = CASE WHEN "QueryCount" + 1 >= "QueryLimit"
                                             THEN {(int)GrantStatus.Consumed} ELSE "Status" END,
                             "UpdatedAt" = {input.Now}, "LastAccessedAt" = {input.Now},
                             "LastAccessIp" = {input.Ip}, "LastAccessHostname" = {input.Hostname}
                         WHERE "Id" = {input.GrantId}
                           AND "OrganizationId" = {input.OrganizationId}
                           AND "AgentId" = {input.AgentId}
                           AND "VaultId" = {input.VaultId}
                           AND "Status" = {(int)GrantStatus.Active}
                           AND "AgentAccessEpoch" = {input.AgentAccessEpoch}
                           AND EXISTS (
                               SELECT 1 FROM "Agents"
                               WHERE "Id" = {input.AgentId}
                                 AND "OrganizationId" = {input.OrganizationId}
                                 AND "Status" = {(int)AgentStatus.Active}
                                 AND "AccessEpoch" = {input.AgentAccessEpoch})
                           AND (
                               ({(int)input.Type} = {(int)GrantType.Granular} AND EXISTS (
                                   SELECT 1
                                   FROM "VaultEntries" entry
                                   JOIN "GrantEntryEnvelopes" envelope
                                     ON envelope."OrganizationId" = entry."OrganizationId"
                                    AND envelope."VaultId" = entry."VaultId"
                                    AND envelope."EntryId" = entry."Id"
                                   WHERE entry."OrganizationId" = {input.OrganizationId}
                                     AND entry."VaultId" = {input.VaultId}
                                     AND entry."Id" = {input.EntryId}
                                     AND entry."State" = {(int)EntryState.Active}
                                     AND entry."CurrentRevision" = envelope."EntryRevision"
                                     AND envelope."GrantId" = {input.GrantId}
                                     AND envelope."EntryRevision" = {materialEntryRevision}
                                     AND envelope."GrantEnvelopeRevision" = {materialGrantEnvelopeRevision}))
                               OR ({(int)input.Type} = {(int)GrantType.Full} AND EXISTS (
                                   SELECT 1
                                   FROM "VaultEntries" entry
                                   JOIN "VaultEntryKeys" entry_key
                                     ON entry_key."OrganizationId" = entry."OrganizationId"
                                    AND entry_key."VaultId" = entry."VaultId"
                                    AND entry_key."EntryId" = entry."Id"
                                    AND entry_key."KeyVersion" = entry."CurrentKeyVersion"
                                   JOIN "AgentWrappedVaultKeys" wrapped_vk
                                     ON wrapped_vk."OrganizationId" = entry."OrganizationId"
                                    AND wrapped_vk."VaultId" = entry."VaultId"
                                    AND wrapped_vk."GrantId" = {input.GrantId}
                                   WHERE entry."OrganizationId" = {input.OrganizationId}
                                     AND entry."VaultId" = {input.VaultId}
                                     AND entry."Id" = {input.EntryId}
                                     AND entry."State" = {(int)EntryState.Active}
                                     AND entry."CurrentRevision" = {materialEntryRevision}
                                     AND wrapped_vk."AgentId" = {input.AgentId}
                                     AND wrapped_vk."AgentAccessEpoch" = {input.AgentAccessEpoch}
                                     AND wrapped_vk."VaultKeyVersion" = {fullMaterialVaultKeyVersionValue}
                                     AND entry_key."WrapperRevision" = {fullMaterialEntryKeyWrapperRevisionValue}
                                     AND wrapped_vk."VaultKeyVersion" = entry_key."WrappingKeyVersion")))
                           AND "QueryCount" < "QueryLimit"
                         RETURNING "QueryCount", "Status"
                     ), deleted_granular AS (
                         DELETE FROM "GrantEntryEnvelopes"
                         WHERE "OrganizationId" = {input.OrganizationId}
                           AND "VaultId" = {input.VaultId}
                           AND "GrantId" = {input.GrantId}
                           AND {(int)input.Type} = {(int)GrantType.Granular}
                           AND EXISTS (SELECT 1 FROM updated WHERE "Status" = {(int)GrantStatus.Consumed})
                     ), deleted_full AS (
                         DELETE FROM "AgentWrappedVaultKeys"
                         WHERE "OrganizationId" = {input.OrganizationId}
                           AND "VaultId" = {input.VaultId}
                           AND "GrantId" = {input.GrantId}
                           AND {(int)input.Type} = {(int)GrantType.Full}
                           AND EXISTS (SELECT 1 FROM updated WHERE "Status" = {(int)GrantStatus.Consumed})
                     )
                     SELECT "QueryCount" AS "Value" FROM updated
                     """)
                .ToListAsync(ct);

            if (newCount.Count == 0)
            {
                var currentEpochGrant = await domainReadContext.Grants
                    .Where(grant =>
                        grant.Id == input.GrantId
                        && grant.OrganizationId == input.OrganizationId
                        && grant.AgentId == input.AgentId
                        && grant.VaultId == input.VaultId
                        && grant.AgentAccessEpoch == input.AgentAccessEpoch
                        && domainReadContext.Agents.Any(agent =>
                            agent.Id == input.AgentId
                            && agent.OrganizationId == input.OrganizationId
                            && agent.Status == AgentStatus.Active
                            && agent.AccessEpoch == input.AgentAccessEpoch))
                    .Select(grant => new { grant.Status })
                    .SingleOrDefaultAsync(ct);
                if (currentEpochGrant is null
                    || (currentEpochGrant.Status != GrantStatus.Active
                        && currentEpochGrant.Status != GrantStatus.Consumed))
                {
                    return new CredentialDeliveryResult.Denied(CredentialDenialReasons.NoActiveGrant);
                }

                if (currentEpochGrant.Status == GrantStatus.Active)
                {
                    var materialStillCurrent = input.Type == GrantType.Full
                        ? await domainReadContext.AgentWrappedVaultKeys.AnyAsync(wrapped =>
                            wrapped.OrganizationId == input.OrganizationId
                            && wrapped.VaultId == input.VaultId
                            && wrapped.GrantId == input.GrantId
                            && wrapped.AgentId == input.AgentId
                            && wrapped.AgentAccessEpoch == input.AgentAccessEpoch
                            && wrapped.VaultKeyVersion == expectedFullVaultKeyVersion
                            && domainReadContext.EntryKeys.Any(key =>
                                key.OrganizationId == input.OrganizationId
                                && key.VaultId == input.VaultId
                                && key.EntryId == input.EntryId
                                && key.KeyVersion == activeEntry.CurrentKeyVersion
                                && key.WrapperRevision == expectedFullEntryKeyWrapperRevision
                                && key.WrappingKeyVersion == wrapped.VaultKeyVersion), ct)
                        : await domainReadContext.GrantEntryEnvelopes.AnyAsync(
                            envelope =>
                                envelope.OrganizationId == input.OrganizationId
                                && envelope.VaultId == input.VaultId
                                && envelope.GrantId == input.GrantId
                                && envelope.EntryId == input.EntryId
                                && envelope.EntryRevision == materialEntryRevision
                                && envelope.GrantEnvelopeRevision == materialGrantEnvelopeRevision
                                && domainReadContext.Entries.Any(entry =>
                                    entry.OrganizationId == input.OrganizationId
                                    && entry.VaultId == input.VaultId
                                    && entry.Id == input.EntryId
                                    && entry.State == EntryState.Active
                                    && entry.CurrentRevision.Value == envelope.EntryRevision),
                            ct);
                    if (!materialStillCurrent)
                    {
                        return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
                    }
                }

                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.QueryLimit);
            }

            remainingUses = input.QueryLimit.Value - newCount[0];

            // The use that reaches the limit consumes the grant. This retrieve still succeeds.
            consumed = newCount[0] >= input.QueryLimit.Value;
        }
        else
        {
            // Lifetime / time-based grant: no counter, but still record the last-access stamp.
            var updated = await domainWriteContext.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "Grants"
                 SET "LastAccessedAt" = {input.Now}, "LastAccessIp" = {input.Ip}, "LastAccessHostname" = {input.Hostname}
                 WHERE "Id" = {input.GrantId}
                   AND "OrganizationId" = {input.OrganizationId}
                   AND "AgentId" = {input.AgentId}
                   AND "VaultId" = {input.VaultId}
                   AND "Status" = {(int)GrantStatus.Active}
                   AND "AgentAccessEpoch" = {input.AgentAccessEpoch}
                   AND EXISTS (
                       SELECT 1 FROM "Agents"
                       WHERE "Id" = {input.AgentId}
                         AND "OrganizationId" = {input.OrganizationId}
                         AND "Status" = {(int)AgentStatus.Active}
                         AND "AccessEpoch" = {input.AgentAccessEpoch})
                   AND (
                       ({(int)input.Type} = {(int)GrantType.Granular} AND EXISTS (
                           SELECT 1
                           FROM "VaultEntries" entry
                           JOIN "GrantEntryEnvelopes" envelope
                             ON envelope."OrganizationId" = entry."OrganizationId"
                            AND envelope."VaultId" = entry."VaultId"
                            AND envelope."EntryId" = entry."Id"
                           WHERE entry."OrganizationId" = {input.OrganizationId}
                             AND entry."VaultId" = {input.VaultId}
                             AND entry."Id" = {input.EntryId}
                             AND entry."State" = {(int)EntryState.Active}
                             AND entry."CurrentRevision" = envelope."EntryRevision"
                             AND envelope."GrantId" = {input.GrantId}
                             AND envelope."EntryRevision" = {materialEntryRevision}
                             AND envelope."GrantEnvelopeRevision" = {materialGrantEnvelopeRevision}))
                       OR ({(int)input.Type} = {(int)GrantType.Full} AND EXISTS (
                           SELECT 1
                           FROM "VaultEntries" entry
                           JOIN "VaultEntryKeys" entry_key
                             ON entry_key."OrganizationId" = entry."OrganizationId"
                            AND entry_key."VaultId" = entry."VaultId"
                            AND entry_key."EntryId" = entry."Id"
                            AND entry_key."KeyVersion" = entry."CurrentKeyVersion"
                           JOIN "AgentWrappedVaultKeys" wrapped_vk
                             ON wrapped_vk."OrganizationId" = entry."OrganizationId"
                            AND wrapped_vk."VaultId" = entry."VaultId"
                            AND wrapped_vk."GrantId" = {input.GrantId}
                           WHERE entry."OrganizationId" = {input.OrganizationId}
                             AND entry."VaultId" = {input.VaultId}
                             AND entry."Id" = {input.EntryId}
                             AND entry."State" = {(int)EntryState.Active}
                             AND entry."CurrentRevision" = {materialEntryRevision}
                             AND wrapped_vk."AgentId" = {input.AgentId}
                             AND wrapped_vk."AgentAccessEpoch" = {input.AgentAccessEpoch}
                             AND wrapped_vk."VaultKeyVersion" = {fullMaterialVaultKeyVersionValue}
                             AND entry_key."WrapperRevision" = {fullMaterialEntryKeyWrapperRevisionValue}
                             AND wrapped_vk."VaultKeyVersion" = entry_key."WrappingKeyVersion")))
                 """,
                ct);
            if (updated == 0)
            {
                var activeGrantStillExists = await domainReadContext.Grants.AnyAsync(
                    grant =>
                        grant.Id == input.GrantId
                        && grant.OrganizationId == input.OrganizationId
                        && grant.AgentId == input.AgentId
                        && grant.VaultId == input.VaultId
                        && grant.Status == GrantStatus.Active
                        && grant.AgentAccessEpoch == input.AgentAccessEpoch
                        && domainReadContext.Agents.Any(agent =>
                            agent.Id == input.AgentId
                            && agent.OrganizationId == input.OrganizationId
                            && agent.Status == AgentStatus.Active
                            && agent.AccessEpoch == input.AgentAccessEpoch),
                    ct);
                if (!activeGrantStillExists)
                {
                    return new CredentialDeliveryResult.Denied(CredentialDenialReasons.NoActiveGrant);
                }

                return new CredentialDeliveryResult.Denied(CredentialDenialReasons.MaterialUnavailable);
            }
        }

        return new CredentialDeliveryResult.Granted(
            material?.GrantEnvelopeRevision ?? 0,
            materialEntryRevision,
            material?.ProtocolVersion ?? VaultProtocol.CurrentVersion,
            material?.CryptoSuiteId ?? string.Empty,
            material?.GrantKeyVersion ?? 0,
            material?.MemberKeyGeneration ?? 0,
            material?.RecipientAgentKeyVersion ?? 0,
            deliveryPolicy,
            material?.FieldIds.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [],
            material?.EncodedSuitePayload ?? [],
            material?.AgentWrappedGrantDek ?? [],
            material?.WrapperSuiteId ?? string.Empty,
            material?.AgentKeyFingerprint ?? [],
            material?.ExpiresAt,
            material?.RemainingUses,
            agentWrappedVaultKey,
            entryKey,
            memberSecret,
            remainingUses,
            consumed,
            agentName ?? CredentialAccessedEvent.UnknownAgent,
            vaultName);
    }
}
