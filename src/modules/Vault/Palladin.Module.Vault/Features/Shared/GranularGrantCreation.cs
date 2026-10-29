using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

internal sealed record StagingFailure(int StatusCode, string Message);

internal sealed record GranularGrantStaging(Guid GrantId, StagingFailure? Failure);

// Stages a proactive GRANULAR grant (or approval of the matching pending request) on the caller's write
// context without committing. A caller that created or updated the Entry in the same unit of work passes
// the tracked Entry so the grant binds that exact revision.
internal static class GranularGrantCreation
{
    internal static async Task<GranularGrantStaging> StageAsync(
        VaultDomainWriteContext domainWriteContext,
        CreateGranularGrantRequest req,
        Guid organizationId,
        Guid userId,
        Instant now,
        CancellationToken ct,
        VaultEntry? stagedEntry = null)
    {
        var agent = await domainWriteContext.Agents
            .SingleOrDefaultAsync(a => a.Id == req.AgentId && a.OrganizationId == organizationId, ct);
        if (agent is null)
        {
            return Fail(StatusCodes.Status404NotFound, "Agent was not found.");
        }

        if (agent.Status != AgentStatus.Active)
        {
            return Fail(StatusCodes.Status400BadRequest, "Agent is not active.");
        }

        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == req.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            return Fail(StatusCodes.Status409Conflict, "Agent deactivation is in progress.");
        }

        var vault = await domainWriteContext.Vaults.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        var entry = stagedEntry is not null && stagedEntry.VaultId == req.VaultId && stagedEntry.Id == req.EntryId
            ? stagedEntry
            : await domainWriteContext.Entries.SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.EntryId, ct);
        if (vault is null || entry is null)
        {
            return Fail(StatusCodes.Status404NotFound, "Vault or Entry was not found.");
        }

        if (entry.State != EntryState.Active)
        {
            return Fail(StatusCodes.Status409Conflict, "Entry is no longer active.");
        }

        if (await domainWriteContext.HasActiveEntryCoverageAsync(
                req.AgentId, agent.AccessEpoch, req.VaultId, req.EntryId, excludingGrantId: null, ct)
            || (stagedEntry is not null && await domainWriteContext.Grants.AnyAsync(grant =>
                grant.OrganizationId == organizationId
                && grant.AgentId == req.AgentId
                && grant.AgentAccessEpoch == agent.AccessEpoch
                && grant.VaultId == req.VaultId
                && grant.Status == GrantStatus.Active
                && grant is FullGrant
                && grant.AgentWrappedVaultKey != null, ct)))
        {
            throw new AgentAlreadyHasActiveAccessException("entry");
        }

        var expirySource = ExpirySource.From(req.ExpiresAt, req.QueryLimit);
        var names = await domainWriteContext.ResolveAsync(req.AgentId, req.EntryId, req.VaultId, userId, ct);
        GrantEntryScope scope;
        try
        {
            scope = ToScope(req, organizationId, agent.PublicKey, vault.MemberKeyGeneration.Value,
                agent.RecipientKeyVersion, entry.CurrentRevision.Value);
        }
        catch (StaleGrantEnvelopeException)
        {
            return Fail(StatusCodes.Status409Conflict, "Grant envelope is invalid or stale.");
        }
        catch (Exception ex) when (ex is FormatException or DomainException)
        {
            return Fail(StatusCodes.Status400BadRequest, "Grant envelope is invalid or stale.");
        }

        var pending = await domainWriteContext.FindPendingForEntryAsync(
            req.AgentId, agent.AccessEpoch, req.VaultId, req.EntryId, ct);
        if (pending is not null)
        {
            if (req.GrantId != pending.Id)
            {
                return Fail(StatusCodes.Status409Conflict, "Grant envelope must target the existing pending request.");
            }

            pending.Approve(userId, names, scope, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods, now);
            agent.FenceAccessMutation();
            vault.FenceAccessMutation(userId, now);
            return new GranularGrantStaging(pending.Id, null);
        }

        var grant = GranularGrant.CreateProactively(
            req.GrantId, req.VaultId, organizationId, req.AgentId, agent.PublicKey, req.EntryId,
            scope, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods, userId, names, now,
            agent.AccessEpoch);
        domainWriteContext.Add(grant);
        agent.FenceAccessMutation();
        vault.FenceAccessMutation(userId, now);
        return new GranularGrantStaging(grant.Id, null);
    }

    internal static bool IsGrantIdentifierConflict(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_Grants",
        };

    private static GranularGrantStaging Fail(int statusCode, string message) =>
        new(Guid.Empty, new StagingFailure(statusCode, message));

    private static GrantEntryScope ToScope(
        CreateGranularGrantRequest req,
        Guid organizationId,
        string agentPublicKey,
        uint memberKeyGeneration,
        uint recipientAgentKeyVersion,
        ulong currentEntryRevision)
    {
        var contract = req.GrantEntry;
        if (contract.OrganizationId != organizationId || contract.VaultId != req.VaultId
            || contract.EntryId != req.EntryId || contract.GrantId != req.GrantId
            || contract.ExpiresAt != req.ExpiresAt || contract.RemainingUses != req.QueryLimit
            || contract.GrantEnvelopeRevision != "1" || contract.GrantKeyVersion != 1
            || contract.MemberKeyGeneration != memberKeyGeneration
            || contract.RecipientAgentKeyVersion != recipientAgentKeyVersion)
        {
            throw new DomainException("Grant envelope scope or lifecycle is invalid.");
        }
        if (contract.EntryRevision != currentEntryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new StaleGrantEnvelopeException();
        }

        var scope = GrantEnvelopeContractMapper.ToDomain(contract, req.Methods, req.AgentId);
        var fingerprint = VaultKeyFingerprint.Compute(
            Convert.FromBase64String(agentPublicKey), VaultKeyKind.AgentX25519);
        if (!scope.Envelope!.AgentKeyFingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            throw new DomainException("Grant envelope Agent key fingerprint is invalid.");
        }
        scope.SetFieldSelectionMode(req.FieldSelectionMode);
        return scope;
    }

    private sealed class StaleGrantEnvelopeException()
        : ConflictException("Grant envelope entry revision is stale.");
}
