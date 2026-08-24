using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

// TPH (Table-Per-Hierarchy) base for grants. The grant TYPE (FullGrant / GranularGrant) is an
// authorization concern — FULL skips per-request approval and may cover any entry of the vault,
// GRANULAR covers exactly one entry. Their crypto material intentionally differs: GRANULAR owns one
// per-entry grant envelope, while FULL owns one Agent-wrapped Vault key.
// Inheritance (not a GrantMode enum on a single class) is used here because the variance is
// structural + behavioral (different FK, different Covers() semantics, different factories),
// which is exactly the case EF Core TPH is for.
internal abstract class Grant : EventEntityBase
{
    public Guid Id { get; protected set; }
    public Guid VaultId { get; protected set; }
    public Guid OrganizationId { get; protected set; }
    public Guid AgentId { get; protected set; }
    public uint AgentAccessEpoch { get; protected set; }
    public string AgentPublicKey { get; protected set; } = string.Empty;
    public GrantStatus Status { get; protected set; }
    public GrantRequestType? RequestType { get; protected set; }

    // While Pending this holds the methods the agent REQUESTED; Approve overwrites it with the
    // user's final (possibly narrowed or widened) decision. Delivery rejects any method outside
    // this set (403 method-not-allowed).
    public GrantMethods Methods { get; protected set; }
    internal EncryptedReasonEnvelope? EncryptedReason { get; set; }

    internal bool InvalidateReasonEncryptedToRetiredAgentMessageKey(uint retiredKeyVersion)
    {
        if (Status != GrantStatus.Pending || EncryptedReason?.AgentMessageKeyVersion != retiredKeyVersion)
        {
            return false;
        }

        EncryptedReason = null;
        return true;
    }
    public Instant? ExpiresAt { get; protected set; }
    public int? QueryLimit { get; protected set; }
    public int QueryCount { get; protected set; }
    public string ExpirySource { get; protected set; } = string.Empty;
    public Instant CreatedAt { get; protected set; }

    // Nullable: null while the grant is awaiting approval (created by an agent request — the actor is
    // AgentId, not a user). Set to the approving/creating user on Approve or proactive create.
    public Guid? CreatedBy { get; protected set; }
    public Instant? RevokedAt { get; protected set; }
    public Guid? RevokedBy { get; protected set; }
    public bool RevokedBySystem { get; protected set; }
    public Instant? DeniedAt { get; protected set; }
    public Guid? DeniedBy { get; protected set; }

    // Last successful credential delivery to the agent: when, and the security context (source IP /
    // reported hostname) of that access. Null until the first delivery. Updated atomically with the
    // query counter in the delivery path (DeliverCredential / GetOrRequestCredential).
    public Instant? LastAccessedAt { get; protected set; }
    public string? LastAccessIp { get; protected set; }
    public string? LastAccessHostname { get; protected set; }

    public Instant UpdatedAt { get; protected set; }

    internal ICollection<GrantEntryScope> GrantEntryScopes { get; set; } = [];
    internal AgentWrappedVaultKey? AgentWrappedVaultKey { get; set; }

    public abstract GrantType Type { get; }

    // Checks whether this grant authorizes access to a requested entry.
    public abstract bool Covers(Guid entryId);

    // User-initiated revoke. Fail-fast on an incompatible status so a business-logic error surfaces
    // immediately (mapped to 409). `names` are denormalized for the revoked event (caller resolves them).
    internal void Revoke(Guid revokedBy, GrantNames names, Instant now)
    {
        if (Status is not (GrantStatus.Active or GrantStatus.Pending))
        {
            throw new InvalidGrantStateTransitionException(GrantStatusTransition.Revoke, Status);
        }

        RevokeCore(revokedBy, isSystem: false, names, now);
    }

    // System-initiated revoke (e.g. cascade on agent deactivation). No user actor — RevokedBy is null
    // and RevokedBySystem is true, avoiding a magic system GUID. Idempotent no-op on a non-revocable
    // status: a concurrent revoke/expire must not break the cascade consumer (no MassTransit retry loop).
    internal void RevokeBySystem(GrantNames names, Instant now)
    {
        if (Status is not (GrantStatus.Active or GrantStatus.Pending))
        {
            return;
        }

        RevokeCore(revokedBy: null, isSystem: true, names, now);
    }

    internal void RemoveEntryAccess(Guid entryId, Instant now)
    {
        foreach (var scope in GrantEntryScopes.Where(x => x.EntryId == entryId).ToList())
        {
            scope.DeleteEnvelope();
        }

        if (this is GranularGrant granular
            && granular.EntryId == entryId
            && Status is GrantStatus.Active or GrantStatus.Pending)
        {
            RevokeBySystem(
                new GrantNames(
                    GrantNames.UnknownAgent,
                    GrantNames.UnknownEntry,
                    string.Empty,
                    GrantNames.SystemActor),
                now);
            EncryptedReason = null;
        }
    }

    internal void DeleteAgentEnvelopes()
    {
        foreach (var scope in GrantEntryScopes)
        {
            scope.DeleteEnvelope();
        }

        AgentWrappedVaultKey = null;
    }

    internal void RemoveEntryScope(Guid entryId, Instant now)
    {
        RemoveEntryAccess(entryId, now);
        foreach (var scope in GrantEntryScopes.Where(x => x.EntryId == entryId).ToList())
        {
            GrantEntryScopes.Remove(scope);
        }
    }

    private void RevokeCore(Guid? revokedBy, bool isSystem, GrantNames names, Instant now)
    {
        Status = GrantStatus.Revoked;
        RevokedBy = revokedBy;
        RevokedBySystem = isSystem;
        RevokedAt = now;
        UpdatedAt = now;
        DeleteDeliveryEnvelopes();

        EmitRevoked(names);
    }

    // Approve a pending grant. The approving user's client supplies per-entry crypto material
    // (re-wrapped DEK) — the server stores ciphertext only and never wraps VK. Sets the chosen
    // expiry policy (at most one of ExpiresAt / QueryLimit; neither means Lifetime).
    internal void Approve(
        Guid approvedBy,
        GrantNames names,
        GrantEntryScope scope,
        Instant? expiresAt,
        int? queryLimit,
        string expirySource,
        GrantMethods? methods,
        Instant now)
    {
        if (Status != GrantStatus.Pending)
        {
            throw new InvalidGrantStateTransitionException(GrantStatusTransition.Approve, Status);
        }

        GrantEntryScopes.Add(scope);

        Status = GrantStatus.Active;
        CreatedBy = approvedBy;
        ExpiresAt = expiresAt;
        QueryLimit = queryLimit;
        ExpirySource = expirySource;
        Methods = methods ?? Methods;
        UpdatedAt = now;
        EmitApproved(names);
    }

    internal void Deny(Guid deniedBy, GrantNames names, Instant now)
    {
        if (Status != GrantStatus.Pending)
        {
            throw new InvalidGrantStateTransitionException(GrantStatusTransition.Deny, Status);
        }

        Status = GrantStatus.Denied;
        DeniedBy = deniedBy;
        DeniedAt = now;
        UpdatedAt = now;
        DeleteDeliveryEnvelopes();

        EmitDenied(names);
    }

    // Time-based expiry is driven by the expiry cron. Only Active grants expire; Consumed,
    // Revoked, Denied and Pending are untouched. Idempotent: a no-op if not Active.
    // entryLabel is resolved upfront by the caller (FULL grants pass null); audit consumers render
    // "what expired" without a re-resolve hop.
    internal void Expire(Instant now, string? entryLabel)
    {
        if (Status != GrantStatus.Active)
        {
            return;
        }

        Status = GrantStatus.Expired;
        UpdatedAt = now;
        DeleteDeliveryEnvelopes();

        EmitExpired(entryLabel);
    }

    private void DeleteDeliveryEnvelopes()
    {
        foreach (var scope in GrantEntryScopes)
        {
            scope.DeleteEnvelope();
        }

        AgentWrappedVaultKey = null;
    }

    protected void EmitCreated(GrantNames names) =>
        AddEvent(new GrantCreatedEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            (this as GranularGrant)?.EntryId,
            Type,
            Status,
            ExpirySource,
            ExpiresAt,
            QueryLimit,
            Methods,
            CreatedBy,
            names.AgentName,
            names.EntryLabel,
            names.VaultName,
            names.ActorName,
            UpdatedAt));

    private void EmitRevoked(GrantNames names)
    {
        var durationActive = RevokedAt.HasValue ? RevokedAt.Value - CreatedAt : Duration.Zero;
        AddEvent(new GrantRevokedEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            (this as GranularGrant)?.EntryId,
            Type,
            RevokedBy,
            RevokedBySystem,
            names.AgentName,
            names.EntryLabel,
            names.VaultName,
            names.ActorName,
            (long)durationActive.TotalSeconds,
            UpdatedAt));
    }

    protected void EmitRequested(Guid entryId, GrantNames names) =>
        AddEvent(new GrantRequestedEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            entryId,
            names.AgentName,
            names.EntryLabel ?? GrantNames.UnknownEntry,
            names.VaultName,
            Methods,
            UpdatedAt));

    private void EmitApproved(GrantNames names) =>
        AddEvent(new GrantApprovedEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            (this as GranularGrant)?.EntryId,
            CreatedBy!.Value,
            Type,
            names.AgentName,
            names.EntryLabel,
            names.VaultName,
            names.ActorName,
            ExpirySource,
            ExpiresAt,
            QueryLimit,
            Methods,
            UpdatedAt));

    private void EmitDenied(GrantNames names) =>
        AddEvent(new GrantDeniedEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            DeniedBy!.Value,
            (this as GranularGrant)?.EntryId ?? Guid.Empty,
            names.AgentName,
            names.EntryLabel ?? GrantNames.UnknownEntry,
            names.VaultName,
            names.ActorName,
            Methods,
            UpdatedAt));

    private void EmitExpired(string? entryLabel)
    {
        var ttlSeconds = ExpiresAt.HasValue ? (long)(ExpiresAt.Value - CreatedAt).TotalSeconds : (long?)null;
        AddEvent(new GrantExpiredEvent(
            Id,
            VaultId,
            OrganizationId,
            AgentId,
            (this as GranularGrant)?.EntryId,
            entryLabel,
            Type,
            ttlSeconds,
            UpdatedAt));
    }
}
