using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record DeliverCredentialRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }

    // How the CLI intends to use the credential. Single flag; bound from the `method`
    // query parameter. Defaults to Get for pre-methods clients.
    [QueryParam]
    public GrantMethods Method { get; init; } = GrantMethods.Get;
}

// The ONLY response in the API that carries ciphertext out. All fields are ciphertext the agent
// decrypts with its own X25519 private key (which never leaves the agent). The server never holds
// the DEK or any plaintext. Never log this payload.
[PublicAPI]
public sealed record DeliverCredentialResponse(
    Guid OrganizationId,
    Guid VaultId,
    Guid GrantId,
    Guid AgentId,
    Guid EntryId,
    ushort ApprovedMethods,
    GrantEntryEnvelopeContract GrantEnvelope);

[UsedImplicitly]
internal sealed class DeliverCredentialValidator : Validator<DeliverCredentialRequest>
{
    public DeliverCredentialValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Method).Must(m => m.IsSingleMethod());
    }
}

[PublicAPI]
internal sealed class DeliverCredentialEndpoint(
    VaultDomainReadContext domainReadContext,
    CredentialDeliveryService deliveryService,
    IEnumerable<IEventPublisher> eventPublishers,
    ILogger<DeliverCredentialEndpoint> logger,
    IClock clock) : Endpoint<DeliverCredentialRequest, DeliverCredentialResponse>
{
    public override void Configure()
    {
        Get("api/agent/vaults/{vaultId:guid}/credentials/{entryId:guid}");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Deliver an encrypted credential to an agent";
            summary.Description = "Returns the canonical authenticated grant envelope and agent-wrapped DEK for an entry the agent has an active grant for. The agent decrypts locally with its X25519 private key. The server is a zero-knowledge relay and never receives entry labels, URL domains, plaintext secrets, or client keys.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(DeliverCredentialRequest req, CancellationToken ct)
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

        // Active grant for this agent covering the requested entry. GRANULAR covers its own EntryId;
        // FULL covers any entry of the vault. Read ciphertext columns only — no decryption anywhere.
        var grant = await domainReadContext.Grants
            .Where(g => g.AgentId == agentId.Value
                        && g.OrganizationId == organizationId.Value
                        && g.AgentAccessEpoch == accessEpoch.Value
                        && g.VaultId == req.VaultId
                        && domainReadContext.Agents.Any(agent =>
                            agent.Id == agentId.Value
                            && agent.OrganizationId == organizationId.Value
                            && agent.Status == AgentStatus.Active
                            && agent.AccessEpoch == accessEpoch.Value)
                        && (g.Status == GrantStatus.Active || g.Status == GrantStatus.Consumed)
                        && ((g is FullGrant && g.GrantEntryScopes.Any(scope => scope.EntryId == req.EntryId
                            && (g.Status == GrantStatus.Consumed || scope.Envelope != null)))
                            || (g as GranularGrant)!.EntryId == req.EntryId))
            .Select(g => new
            {
                g.Id,
                g.OrganizationId,
                g.Status,
                g.ExpiresAt,
                g.QueryLimit,
                g.Methods,
                g.AgentAccessEpoch,
                Type = g is FullGrant ? GrantType.Full : GrantType.Granular,
            })
            .FirstOrDefaultAsync(ct);

        // Only Active and Consumed grants are matched above; any other status (revoked / denied /
        // pending / expired) or no grant at all is treated as "no grant" -> 404.
        if (grant is null)
        {
            await DenyAsync(req, agentId.Value, CredentialDenialReasons.NoActiveGrant, now, ct);
            await Send.NotFoundAsync(ct);
            return;
        }

        // Already used up: distinct from "no grant" — the agent had access and exhausted it -> 429.
        if (grant.Status == GrantStatus.Consumed)
        {
            await DenyAsync(req, agentId.Value, CredentialDenialReasons.QueryLimit, now, ct);
            await Send.ErrorsAsync(429, ct);
            return;
        }

        // BUG#2 fix: read everything needed for the response BEFORE the atomic increment. After a use
        // is counted, the only remaining work is the Consumed flip + event publish + OkAsync, so an
        // exception cannot burn a use without delivering the secret.
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var hostname = HttpContext.Request.Headers[AgentAuthenticationOptions.AgentHostnameHeader].FirstOrDefault();

        var result = await deliveryService.ExecuteAsync(
            new CredentialDeliveryInput(
                grant.Id, grant.OrganizationId, agentId.Value, req.VaultId, req.EntryId,
                accessEpoch.Value, grant.ExpiresAt, grant.QueryLimit, grant.Type, grant.Methods, req.Method, ip, hostname, now),
            ct);

        switch (result)
        {
            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.NoActiveGrant }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.NoActiveGrant, now, ct);
                await Send.NotFoundAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.Expired }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.Expired, now, ct);
                await Send.ForbiddenAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.MethodNotAllowed }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.MethodNotAllowed, now, ct);
                await Send.ForbiddenAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.ScriptExecOnly }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.ScriptExecOnly, now, ct);
                await Send.ForbiddenAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.CreditCardInjectOnly }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.CreditCardInjectOnly, now, ct);
                await Send.ForbiddenAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.MaterialUnavailable }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.MaterialUnavailable, now, ct);
                await Send.NotFoundAsync(ct);
                return;

            case CredentialDeliveryResult.Denied { Reason: CredentialDenialReasons.QueryLimit }:
                await DenyAsync(req, agentId.Value, CredentialDenialReasons.QueryLimit, now, ct);
                await Send.ErrorsAsync(429, ct);
                return;

            case CredentialDeliveryResult.Granted granted:
                // BUG#3 fix: deliver the response BEFORE publishing CredentialAccessedEvent / GrantConsumedEvent.
                // The use has already been counted (atomic UPDATE inside the service); if a broker outage made
                // PublishAsync throw before the response was sent, the use would be permanently burned with no
                // secret reaching the agent. Audit/notification publish is now best-effort post-response — a
                // broker failure costs visibility, not the credential.
                await Send.OkAsync(
                    new DeliverCredentialResponse(
                        grant.OrganizationId,
                        req.VaultId,
                        grant.Id,
                        agentId.Value,
                        req.EntryId,
                        (ushort)grant.Methods,
                        GrantDeliveryContractMapper.ToContract(granted, grant.OrganizationId, req.VaultId,
                            grant.Id, agentId.Value, req.EntryId, grant.Methods)),
                    ct);

                // Expiry is type-dependent and mutually exclusive: time-based grants carry ExpiresAt, use-based
                // grants carry RemainingUses. grant.ExpiresAt is null for use-based, so passing it directly
                // keeps the fields mutually exclusive.
                await PublishPostResponseAsync(
                    new CredentialAccessedEvent(
                        grant.Id, req.VaultId, grant.OrganizationId, agentId.Value, req.EntryId, grant.Type,
                        granted.AgentName, string.Empty, granted.VaultName,
                        grant.ExpiresAt, granted.RemainingUses, req.Method, now),
                    ct);

                if (granted.Consumed)
                {
                    // Granular grants are tied to a single entry — populate EntryId/EntryLabel so audit
                    // records "which grant exhausted, and over which entry". FULL grants are not entry-scoped.
                    var consumedEntryId = grant.Type == GrantType.Granular ? req.EntryId : (Guid?)null;
                    string? consumedEntryLabel = null;
                    await PublishPostResponseAsync(
                        new GrantConsumedEvent(grant.Id, req.VaultId, grant.OrganizationId, agentId.Value,
                            consumedEntryId, consumedEntryLabel, now),
                        ct);
                }
                return;
        }
    }

    // TODO: throttle CredentialAccessDenied emission per agent — an attacker
    // enumerating entryIds amplifies audit volume. Tackle together with the anti-scraping velocity throttle.
    private async Task DenyAsync(DeliverCredentialRequest req, Guid agentId, string reason, Instant now, CancellationToken ct) =>
        await PublishAsync(new CredentialAccessDeniedEvent(req.VaultId, agentId, req.EntryId, reason, now), ct);

    private async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : Core.Events.IEvent
    {
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(@event, ct);
        }
    }

    // Best-effort publish that runs AFTER the response body has been written. A broker outage at this
    // point would otherwise propagate out of the handler and surface as a 500 — but the client already
    // has the response. Log and swallow so audit/notification loss never burns a credential delivery.
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
