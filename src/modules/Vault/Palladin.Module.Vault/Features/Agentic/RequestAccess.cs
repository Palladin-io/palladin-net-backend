using System.Security.Cryptography;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RequestAccessRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public EncryptedReasonEnvelopeContract EncryptedReason { get; init; } = null!;

    // Methods the agent asks for. The approving user may narrow or widen them. Defaults
    // to Get for pre-methods clients.
    public GrantMethods RequestedMethods { get; init; } = GrantMethods.Get;
}

[PublicAPI]
public sealed record RequestAccessResponse(Guid GrantId, GrantStatus Status);

[UsedImplicitly]
internal sealed class RequestAccessValidator : Validator<RequestAccessRequest>
{
    public RequestAccessValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.EncryptedReason).NotNull();
        RuleFor(x => x.EncryptedReason).SetValidator(new EncryptedReasonEnvelopeContractValidator());
        RuleFor(x => x.RequestedMethods).Must(m => m.IsValidSet());
    }
}

[PublicAPI]
internal sealed class RequestAccessEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<RequestAccessRequest, RequestAccessResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/request-access");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Agent requests access to a single entry";
            summary.Description = "Creates a pending single-entry grant awaiting user approval. No credential material is returned or stored here — the approving user's client supplies the re-wrapped DEK on approval. Authenticated via X-Api-Key + X-Agent-Key.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(RequestAccessRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(a => a.Id == agentId.Value
                        && a.OrganizationId == organizationId.Value
                        && a.AccessEpoch == accessEpoch.Value)
            .Select(a => new { a.OrganizationId, a.Status, a.PublicKey, a.SigningPublicKey, a.AccessEpoch })
            .FirstOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active
            || await domainReadContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId.Value
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == agentId.Value
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var entry = await domainReadContext.Entries
            .Where(e => e.OrganizationId == agent.OrganizationId
                        && e.Id == req.EntryId
                        && e.VaultId == req.VaultId
                        && e.State == EntryState.Active)
            .Select(e => new { e.VaultId, CurrentRevision = e.CurrentRevision.Value })
            .FirstOrDefaultAsync(ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var vault = await domainReadContext.Vaults
            .Where(v => v.Id == req.VaultId)
            .Select(v => new { v.OrganizationId, AgentMessageKeyVersion = v.CurrentAgentMessageKeyVersion.Value,
                v.AgentMessageKeyFingerprint })
            .FirstOrDefaultAsync(ct);
        if (vault is null || vault.OrganizationId != agent.OrganizationId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // TODO: enforce an anti-scraping policy. An agent may hold access to exactly one entry at a
        // time (discovery -> request -> deliver). Enforce rate/velocity limits here (e.g. cap on
        // concurrent pending/active grants per agent, throttle sequential requests) to prevent an
        // agent from exfiltrating secrets one-by-one. Enforce this at the unified access boundary.

        // Short-circuit if the agent already has a covering grant on this entry — Pending GRANULAR
        // on the entry, or an Active grant with material for the current entry revision. Without the FULL check, an
        // agent with FULL coverage could create a Pending Granular here that ApproveGrant would
        // 409 on (HasActiveEntryCoverageAsync), leaving un-approvable noise + misleading
        // user-facing notification.
        var existing = await domainReadContext.Grants
            .Where(g => g.AgentId == agentId.Value
                        && g.VaultId == req.VaultId
                        && g.AgentAccessEpoch == agent.AccessEpoch
                        && ((g.Status == GrantStatus.Pending
                             && g is GranularGrant
                             && ((GranularGrant)g).EntryId == req.EntryId)
                            || (g.Status == GrantStatus.Active
                                && g.GrantEntryScopes.Any(scope => scope.EntryId == req.EntryId
                                    && scope.Envelope != null
                                    && scope.Envelope.EntryRevision == entry.CurrentRevision))))
            .Select(g => new { g.Id, g.Status })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            await Send.OkAsync(new RequestAccessResponse(existing.Id, existing.Status), ct);
            return;
        }

        if (req.EncryptedReason.OrganizationId != agent.OrganizationId
            || req.EncryptedReason.VaultId != req.VaultId
            || req.EncryptedReason.EntryId != req.EntryId
            || req.EncryptedReason.AgentId != agentId.Value
            || req.EncryptedReason.AgentMessageKeyVersion != vault.AgentMessageKeyVersion
            || req.EncryptedReason.RequestedMethods != (ushort)req.RequestedMethods)
        {
            AddError(r => r.EncryptedReason, "Encrypted reason scope or key version is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        EncryptedReasonEnvelope encryptedReason;
        try
        {
            encryptedReason = EncryptedReasonValidator.ToDomain(
                req.EncryptedReason, agent.SigningPublicKey, vault.AgentMessageKeyFingerprint!);
        }
        catch (Exception ex) when (ex is FormatException or Palladin.Core.Types.Exceptions.DomainException or CryptographicException)
        {
            AddError(r => r.EncryptedReason, "Encrypted reason envelope is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (await domainReadContext.Grants.AnyAsync(
                g => g.Id == req.EncryptedReason.GrantRequestId, ct))
        {
            AddError(r => r.EncryptedReason, "Grant request identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var names = await domainReadContext.ResolveAsync(
            agentId.Value, req.EntryId, req.VaultId, actorUserId: null, ct);
        var grant = GranularGrant.RequestAccess(
            req.EncryptedReason.GrantRequestId,
            req.VaultId,
            agent.OrganizationId,
            agentId.Value,
            agent.PublicKey,
            req.EntryId,
            names,
            encryptedReason,
            req.RequestedMethods,
            clock.GetCurrentInstant(),
            accessEpoch.Value);

        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(agent.OrganizationId).SingleAsync(ct);
        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == agent.OrganizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == agentId.Value
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        domainWriteContext.Add(grant);
        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_Grants",
        })
        {
            AddError(r => r.EncryptedReason, "Grant request identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(new RequestAccessResponse(grant.Id, grant.Status), ct);
    }
}
