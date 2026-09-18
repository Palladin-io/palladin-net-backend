using System.Text.Json.Serialization;
using Palladin.Core.Events;
using System.Security.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetOrRequestCredentialRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }

    // Required only when no grant exists yet and one must be requested. Ignored
    // when a grant already exists. Validated in the handler, not the validator, because it is
    // conditional on server state the validator cannot see.
    public EncryptedReasonEnvelopeContract? EncryptedReason { get; init; }

    // How the CLI intends to use the credential. Single flag; Get for clients that predate method selection.
    public GrantMethods Method { get; init; } = GrantMethods.Get;

    // Methods to put on the Pending grant when this call has to create one. Defaults to the
    // delivery method above so a plain `exec` call requests exec access.
    public GrantMethods? RequestedMethods { get; init; }
    public bool IncludeDiscoveryBinding { get; init; }
}

// Discriminated union over `access`. Only the "granted" variant carries ciphertext — and it is the
// ONLY place in the API that does. GRANULAR carries an agent-sealed grant envelope. FULL carries a
// client-created AgentWrappedVaultKey plus the current EntryKey and MemberSecret envelopes. The
// server never unwraps any key or sees plaintext. Never log the granted payload. Optional fields
// are null for every other access state.
[PublicAPI]
public sealed record GetOrRequestCredentialResponse(
    string Access,
    Guid? OrganizationId = null,
    Guid? VaultId = null,
    Guid? AgentId = null,
    uint? AgentAccessEpoch = null,
    ushort? ApprovedMethods = null,
    Guid? EntryId = null,
    GrantType? GrantType = null,
    GrantDeliveryPolicy? DeliveryPolicy = null,
    Instant? ExpiresAt = null,
    GrantEntryEnvelopeContract? GrantEnvelope = null,
    AgentWrappedVaultKeyContract? AgentWrappedVaultKey = null,
    VaultEntryKeyContract? EntryKey = null,
    MemberSecretEnvelopeContract? MemberSecret = null,
    Guid? GrantId = null,
    bool? Created = null,
    int? PollIntervalMs = null,
    int? MaxWaitMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    InjectDiscoveryBindingContract? InjectDiscoveryBinding = null);

[PublicAPI]
public sealed record InjectDiscoveryBindingContract(string EntryRevision, string? AgentDiscoveryRevision);

// Organization approval-wait policy surfaced on `pending` so the agent CLI knows how long, and how
// often, to long-poll for approval — used only when the operator passes no explicit --wait /
// --poll-interval (CLI overrides backend). Configurable via Modules:Vault:GrantApprovalWait.
internal sealed class GrantApprovalWaitOptions
{
    public const string Position = "Modules:Vault:GrantApprovalWait";

    public int PollIntervalSeconds { get; init; } = 30;
    public int MaxWaitSeconds { get; init; } = 180;
}

[UsedImplicitly]
internal sealed class GetOrRequestCredentialValidator : Validator<GetOrRequestCredentialRequest>
{
    public GetOrRequestCredentialValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.EncryptedReason)
            .SetValidator(new EncryptedReasonEnvelopeContractValidator()!)
            .When(x => x.EncryptedReason is not null);
        RuleFor(x => x.Method).Must(m => m.IsSingleMethod());
        RuleFor(x => x.RequestedMethods!.Value).Must(m => m.IsValidSet()).When(x => x.RequestedMethods.HasValue);
    }
}

[PublicAPI]
internal sealed class GetOrRequestCredentialEndpoint(
    VaultDomainWriteContext domainWriteContext,
    CredentialDeliveryService deliveryService,
    IEnumerable<IEventPublisher> eventPublishers,
    IOptions<GrantApprovalWaitOptions> waitOptions,
    ILogger<GetOrRequestCredentialEndpoint> logger,
    IClock clock) : Endpoint<GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>
{
    // Discriminator values for the response `access` field. Stable strings — clients branch on them.
    private const string AccessGranted = "granted";
    private const string AccessUnavailable = "unavailable";
    private const string AccessPending = "pending";
    private const string AccessDenied = "denied";
    private const string AccessExpired = "expired";
    private const string AccessConsumed = "consumed";
    private const string AccessBlocked = "blocked";
    private const string AccessMethodNotAllowed = "method-not-allowed";
    private const string AccessScriptExecOnly = "script-exec-only";
    private const string AccessCreditCardInjectOnly = "credit-card-inject-only";

    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/entries/{entryId:guid}/credential");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Get a credential or request access in one call (unified agent flow)";
            summary.Description = "Single entry point for request, polling, and delivery. The granted variant carries disjoint encrypted material: one canonical grant envelope for GRANULAR, or the Agent-wrapped Vault key plus current Entry key and MemberSecret for FULL. Labels, URL domains, plaintext secrets, and client keys never reach the server. The native Agent decrypts locally.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetOrRequestCredentialRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();

        var agent = await domainWriteContext.Agents
            .Where(a => a.Id == agentId.Value
                        && a.OrganizationId == organizationId.Value
                        && a.AccessEpoch == accessEpoch.Value)
            .Select(a => new { a.OrganizationId, a.Status, a.PublicKey, a.SigningPublicKey, a.AccessEpoch, a.Name })
            .FirstOrDefaultAsync(ct);
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessBlocked), ct);
            return;
        }

        // Org isolation: the entry must live in a vault of the agent's organization. 404 otherwise so
        // a foreign entry is indistinguishable from a non-existent one.
        var entry = await domainWriteContext.Entries
            .Where(e => e.OrganizationId == agent.OrganizationId
                        && e.VaultId == req.VaultId
                        && e.Id == req.EntryId)
            .Select(e => new { e.State, CurrentRevision = e.CurrentRevision.Value })
            .SingleOrDefaultAsync(ct);
        var vault = await domainWriteContext.Vaults
            .Where(v => v.OrganizationId == agent.OrganizationId && v.Id == req.VaultId)
            .Select(v => new { v.OrganizationId, AgentMessageKeyVersion = v.CurrentAgentMessageKeyVersion.Value,
                v.AgentMessageKeyFingerprint })
            .FirstOrDefaultAsync(ct);
        if (entry is null || vault is null || vault.OrganizationId != agent.OrganizationId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entry.State != EntryState.Active)
        {
            await SendAccessAsync(200, new GetOrRequestCredentialResponse(AccessUnavailable), ct);
            return;
        }

        // TODO: add a per-agent anti-scraping and velocity throttle on this unified entry point —
        // it collapses request + poll + deliver, so it is the single richest enumeration vector. Cap
        // concurrent pending/active grants and throttle sequential calls per agent here.

        // All grants covering this entry, regardless of status, newest first. GRANULAR covers its own
        // EntryId; FULL covers any entry of the vault. Over the lifetime of an (agent, entry) pair there
        // may be several rows (re-requests after a terminal state never overwrite the old one — they add
        // a new row). The re-access rule is resolved over the whole set, not a single pick.
        var grants = await domainWriteContext.Grants
            .Where(g => g.OrganizationId == agent.OrganizationId
                        && g.AgentId == agentId.Value
                        && g.AgentAccessEpoch == agent.AccessEpoch
                        && g.VaultId == req.VaultId
                        && ((g is FullGrant)
                            || (g is GranularGrant && ((GranularGrant)g).EntryId == req.EntryId)))
            .Select(g => new
            {
                g.Id,
                g.OrganizationId,
                g.Status,
                g.ExpiresAt,
                g.QueryLimit,
                g.CreatedAt,
                g.Methods,
                g.AgentAccessEpoch,
                Type = g is FullGrant ? GrantType.Full : GrantType.Granular,
                HasCurrentMaterial = (g is FullGrant && g.AgentWrappedVaultKey != null)
                    || (g is GranularGrant && g.GrantEntryScopes.Any(scope => scope.EntryId == req.EntryId
                        && scope.Envelope != null
                        && scope.Envelope.EntryRevision == entry.CurrentRevision)),
            })
            .OrderByDescending(g => g.CreatedAt)
            .ThenByDescending(g => g.Id)
            .ToListAsync(ct);

        // 1) Active (with material) wins — deliver. Newest Active if several.
        var active = grants.FirstOrDefault(g => g.Status == GrantStatus.Active && g.HasCurrentMaterial);
        if (active is not null)
        {
            await DeliverActiveAsync(req, agentId.Value, active.Id, active.OrganizationId, accessEpoch.Value, active.ExpiresAt, active.QueryLimit, active.Type, active.Methods, now, ct);
            return;
        }

        // 2) A pending request is already in flight — report it, never create a second one.
        var pending = grants.FirstOrDefault(g => g.Status == GrantStatus.Pending);
        if (pending is not null)
        {
            await SendAccessAsync(202, Pending(pending.Id), ct);
            return;
        }

        // 3) Only an explicit Denial blocks a re-request, and only while it is the latest decision (a
        // newer re-creatable terminal supersedes an older Denied, so the agent is never permanently
        // stuck on a stale refusal). Every other terminal (Revoked by user OR system,
        // Consumed, Expired, Superseded) is re-requestable and falls through to step 4: a revoke or
        // replacement ends the current access, it does not bar the agent from asking again with a
        // fresh justification.
        var latestTerminal = grants.FirstOrDefault();
        if (latestTerminal is { Status: GrantStatus.Denied })
        {
            await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessDenied), ct);
            return;
        }

        // 4) No live grant and not blocked by a latest Denied (no grant at all, or the newest terminal
        // is Revoked / Consumed / Expired / Superseded) — create a fresh Pending re-request. The old terminal row
        // stays for history/audit.
        await RequestNewGrantAsync(req, agentId.Value, accessEpoch.Value, agent.OrganizationId,
            agent.PublicKey, agent.Name ?? GrantNames.UnknownAgent, agent.SigningPublicKey, vault.AgentMessageKeyVersion,
            vault.AgentMessageKeyFingerprint!, now, ct);
    }

    private async Task DeliverActiveAsync(
        GetOrRequestCredentialRequest req,
        Guid agentId,
        Guid grantId,
        Guid organizationId,
        uint agentAccessEpoch,
        Instant? expiresAt,
        int? queryLimit,
        GrantType type,
        GrantMethods allowedMethods,
        Instant now,
        CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var hostname = HttpContext.Request.Headers[AgentAuthenticationOptions.AgentHostnameHeader].FirstOrDefault();

        var result = await deliveryService.ExecuteAsync(
            new CredentialDeliveryInput(
                grantId, organizationId, agentId, req.VaultId, req.EntryId,
                agentAccessEpoch, expiresAt, queryLimit, type, allowedMethods, req.Method, ip, hostname, now),
            ct);

        switch (result)
        {
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.NoActiveGrant }:
                await DenyAsync(req, agentId, CredentialDenialReasons.NoActiveGrant, now, ct);
                await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessBlocked), ct);
                return;

            // The grant is live but does not whitelist the requested method. Return 403 with its own
            // discriminator so the CLI can tell the agent which methods ARE allowed to suggest a retry.
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.MethodNotAllowed }:
                await DenyAsync(req, agentId, CredentialDenialReasons.MethodNotAllowed, now, ct);
                await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessMethodNotAllowed), ct);
                return;

            // Script entries are exec-only: the grant may whitelist get/inject, but a script's
            // content is never readable or form-injectable by an agent.
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.ScriptExecOnly }:
                await DenyAsync(req, agentId, CredentialDenialReasons.ScriptExecOnly, now, ct);
                await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessScriptExecOnly), ct);
                return;

            // Credit-card material is inject-only: neither get nor exec may return its grant
            // envelope. The authenticated delivery policy is enforced without inspecting the
            // encrypted entry type or field identifiers.
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.CreditCardInjectOnly }:
                await DenyAsync(req, agentId, CredentialDenialReasons.CreditCardInjectOnly, now, ct);
                await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessCreditCardInjectOnly), ct);
                return;

            // Distinct from the Expired *status* case (this is Active-but-stale — cron has not flipped yet).
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.Expired }:
                await DenyAsync(req, agentId, CredentialDenialReasons.Expired, now, ct);
                await SendAccessAsync(403, new GetOrRequestCredentialResponse(AccessExpired), ct);
                return;

            // Ciphertext for the selected grant mode is missing or stale. Fail closed without
            // attempting to substitute material from another grant.
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.MaterialUnavailable }:
                await DenyAsync(req, agentId, CredentialDenialReasons.MaterialUnavailable, now, ct);
                await SendAccessAsync(200, new GetOrRequestCredentialResponse(AccessUnavailable), ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.QueryLimit }:
                await DenyAsync(req, agentId, CredentialDenialReasons.QueryLimit, now, ct);
                await SendAccessAsync(429, new GetOrRequestCredentialResponse(AccessConsumed), ct);
                return;

            case CredentialDeliveryResult.Granted granted:
                // BUG#3 fix: deliver the response BEFORE publishing CredentialAccessedEvent / GrantConsumedEvent
                // (same as DeliverCredential). The use has already been counted; a broker outage on publish
                // must not burn the use without the agent ever seeing the secret.
                await SendAccessAsync(200, new GetOrRequestCredentialResponse(
                    AccessGranted,
                    OrganizationId: organizationId,
                    VaultId: req.VaultId,
                    AgentId: agentId,
                    AgentAccessEpoch: agentAccessEpoch,
                    ApprovedMethods: (ushort)allowedMethods,
                    EntryId: req.EntryId,
                    GrantType: type,
                    DeliveryPolicy: granted.DeliveryPolicy,
                    ExpiresAt: expiresAt,
                    GrantEnvelope: type == GrantType.Granular
                        ? GrantDeliveryContractMapper.ToContract(granted, organizationId, req.VaultId,
                            grantId, agentId, req.EntryId, allowedMethods)
                        : null,
                    AgentWrappedVaultKey: granted.AgentWrappedVaultKey,
                    EntryKey: granted.EntryKey,
                    MemberSecret: granted.MemberSecret,
                    GrantId: grantId,
                    InjectDiscoveryBinding: req.IncludeDiscoveryBinding && req.Method == GrantMethods.Inject
                        ? new InjectDiscoveryBindingContract(
                            granted.EntryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            granted.AgentDiscoveryRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        : null),
                    ct);

                await PublishPostResponseAsync(
                    new CredentialAccessedEvent(
                        grantId, req.VaultId, organizationId, agentId, req.EntryId, type,
                        granted.AgentName, string.Empty, granted.VaultName,
                        expiresAt, granted.RemainingUses, req.Method, now),
                    ct);

                if (granted.Consumed)
                {
                    var consumedEntryId = type == GrantType.Granular ? req.EntryId : (Guid?)null;
                    string? consumedEntryLabel = null;
                    await PublishPostResponseAsync(
                        new GrantConsumedEvent(grantId, req.VaultId, organizationId, agentId,
                            consumedEntryId, consumedEntryLabel, now),
                        ct);
                }
                return;
        }
    }

    private async Task RequestNewGrantAsync(
        GetOrRequestCredentialRequest req,
        Guid agentId,
        uint agentAccessEpoch,
        Guid organizationId,
        string agentPublicKey,
        string agentName,
        string agentSigningPublicKey,
        uint agentMessageKeyVersion,
        byte[] agentMessageKeyFingerprint,
        Instant now,
        CancellationToken ct)
    {
        // Creating a grant requires a justification. This is conditional on state, so check it here.
        if (req.EncryptedReason is null)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var requestedMethods = req.RequestedMethods ?? req.Method;
        if (req.EncryptedReason.OrganizationId != organizationId
            || req.EncryptedReason.VaultId != req.VaultId
            || req.EncryptedReason.EntryId != req.EntryId
            || req.EncryptedReason.AgentId != agentId
            || req.EncryptedReason.AgentMessageKeyVersion != agentMessageKeyVersion
            || req.EncryptedReason.RequestedMethods != (ushort)requestedMethods)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        EncryptedReasonEnvelope encryptedReason;
        try
        {
            encryptedReason = EncryptedReasonValidator.ToDomain(
                req.EncryptedReason, agentSigningPublicKey, agentMessageKeyFingerprint);
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException or CryptographicException)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (await domainWriteContext.Grants.AnyAsync(
                g => g.Id == req.EncryptedReason.GrantRequestId, ct))
        {
            await SendAccessAsync(409, new GetOrRequestCredentialResponse(AccessUnavailable), ct);
            return;
        }

        var names = new GrantNames(agentName, null, string.Empty, null);
        var grant = GranularGrant.RequestAccess(
            req.EncryptedReason.GrantRequestId,
            req.VaultId,
            organizationId,
            agentId,
            agentPublicKey,
            req.EntryId,
            names,
            encryptedReason,
            requestedMethods,
            now,
            agentAccessEpoch);

        domainWriteContext.Add(grant);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_Grants",
        })
        {
            await SendAccessAsync(409, new GetOrRequestCredentialResponse(AccessUnavailable), ct);
            return;
        }

        await SendAccessAsync(202, Pending(grant.Id, created: true), ct);
    }

    // 202 pending, stamped with the org's long-poll policy so the agent CLI knows how long / how
    // often to wait for approval (CLI flags still override these).
    private GetOrRequestCredentialResponse Pending(Guid grantId, bool created = false) =>
        new(AccessPending,
            GrantId: grantId,
            Created: created ? true : null,
            PollIntervalMs: waitOptions.Value.PollIntervalSeconds * 1000,
            MaxWaitMs: waitOptions.Value.MaxWaitSeconds * 1000);

    private async Task SendAccessAsync(int statusCode, GetOrRequestCredentialResponse response, CancellationToken ct) =>
        await Send.ResponseAsync(response, statusCode, ct);

    private async Task DenyAsync(GetOrRequestCredentialRequest req, Guid agentId, string reason, Instant now, CancellationToken ct) =>
        await PublishAsync(new CredentialAccessDeniedEvent(req.VaultId, agentId, req.EntryId, reason, now), ct);

    private async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : Core.Events.IEvent
    {
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(@event, ct);
        }
    }

    // Best-effort publish that runs AFTER the response body has been written. Mirrors DeliverCredential
    // so a broker outage on the audit/notification publish never burns the use after the agent already
    // received the secret.
    private async Task PublishPostResponseAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : Core.Events.IEvent
    {
        try
        {
            await PublishAsync(@event, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Post-response publish of {EventType} failed", typeof(TEvent).Name);
        }
    }
}
