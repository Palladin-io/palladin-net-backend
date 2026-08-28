using System.Security.Cryptography;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record RequestScriptExecutionAccessRequest
{
    public Guid VaultId { get; init; }
    public Guid ScriptEntryId { get; init; }
    public EncryptedReasonEnvelopeContract EncryptedReason { get; init; } = null!;
}

[UsedImplicitly]
internal sealed class RequestScriptExecutionAccessValidator
    : Validator<RequestScriptExecutionAccessRequest>
{
    public RequestScriptExecutionAccessValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.ScriptEntryId).NotEmpty();
        RuleFor(x => x.EncryptedReason).NotNull();
        RuleFor(x => x.EncryptedReason)
            .SetValidator(new EncryptedReasonEnvelopeContractValidator());
    }
}

[PublicAPI]
internal sealed class RequestScriptExecutionAccessEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock)
    : Endpoint<RequestScriptExecutionAccessRequest, RequestAccessResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/scripts/{scriptEntryId:guid}/request-access");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Agent requests execution access to one Script";
            summary.Description = "Creates one pending ScriptExecution grant for the Script. The approving client later supplies one encrypted package containing the Script and all of its references.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(
        RequestScriptExecutionAccessRequest req,
        CancellationToken ct)
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
            .Where(candidate => candidate.Id == agentId.Value
                                && candidate.OrganizationId == organizationId.Value
                                && candidate.AccessEpoch == accessEpoch.Value)
            .Select(candidate => new
            {
                candidate.OrganizationId,
                candidate.Status,
                candidate.PublicKey,
                candidate.SigningPublicKey,
                candidate.AccessEpoch,
            })
            .FirstOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active
            || await HasPendingDeprovisioningAsync(
                organizationId.Value, agentId.Value, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var script = await domainReadContext.Entries
            .Where(entry => entry.OrganizationId == agent.OrganizationId
                            && entry.Id == req.ScriptEntryId
                            && entry.VaultId == req.VaultId
                            && entry.State == EntryState.Active)
            .Select(entry => new { entry.DeliveryPolicy })
            .FirstOrDefaultAsync(ct);
        if (script is null || script.DeliveryPolicy != GrantDeliveryPolicy.ExecOnly)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var vault = await domainReadContext.Vaults
            .Where(candidate => candidate.Id == req.VaultId)
            .Select(candidate => new
            {
                candidate.OrganizationId,
                AgentMessageKeyVersion = candidate.CurrentAgentMessageKeyVersion.Value,
                candidate.AgentMessageKeyFingerprint,
            })
            .FirstOrDefaultAsync(ct);
        if (vault is null || vault.OrganizationId != agent.OrganizationId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var active = await domainReadContext.Grants
            .Where(grant => grant.AgentId == agentId.Value
                            && grant.AgentAccessEpoch == agent.AccessEpoch
                            && grant.VaultId == req.VaultId
                            && grant.Status == GrantStatus.Active
                            && ((grant is FullGrant
                                 && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec
                                 && grant.AgentWrappedVaultKey != null)
                                || (grant is ScriptExecutionGrant
                                    && ((ScriptExecutionGrant)grant).ScriptEntryId == req.ScriptEntryId
                                    && grant.ScriptExecutionPackage != null)))
            .Select(grant => new { grant.Id, grant.Status })
            .FirstOrDefaultAsync(ct);
        if (active is not null)
        {
            await Send.OkAsync(new RequestAccessResponse(active.Id, active.Status), ct);
            return;
        }

        var pending = await domainReadContext.Grants
            .OfType<ScriptExecutionGrant>()
            .Where(grant => grant.AgentId == agentId.Value
                            && grant.AgentAccessEpoch == agent.AccessEpoch
                            && grant.VaultId == req.VaultId
                            && grant.ScriptEntryId == req.ScriptEntryId
                            && grant.Status == GrantStatus.Pending)
            .Select(grant => new { grant.Id, grant.Status })
            .FirstOrDefaultAsync(ct);
        if (pending is not null)
        {
            await Send.OkAsync(new RequestAccessResponse(pending.Id, pending.Status), ct);
            return;
        }

        var terminal = await domainReadContext.Grants
            .OfType<ScriptExecutionGrant>()
            .Where(grant => grant.AgentId == agentId.Value
                            && grant.AgentAccessEpoch == agent.AccessEpoch
                            && grant.VaultId == req.VaultId
                            && grant.ScriptEntryId == req.ScriptEntryId
                            && (grant.Status == GrantStatus.Denied
                                || (grant.Status == GrantStatus.Revoked
                                    && !grant.RevokedBySystem)))
            .OrderByDescending(grant => grant.UpdatedAt)
            .Select(grant => new { grant.Id, grant.Status })
            .FirstOrDefaultAsync(ct);
        if (terminal is not null)
        {
            await Send.OkAsync(new RequestAccessResponse(terminal.Id, terminal.Status), ct);
            return;
        }

        if (req.EncryptedReason.OrganizationId != agent.OrganizationId
            || req.EncryptedReason.VaultId != req.VaultId
            || req.EncryptedReason.EntryId != req.ScriptEntryId
            || req.EncryptedReason.AgentId != agentId.Value
            || req.EncryptedReason.AgentMessageKeyVersion != vault.AgentMessageKeyVersion
            || req.EncryptedReason.RequestedMethods != (ushort)GrantMethods.Exec)
        {
            AddError(request => request.EncryptedReason,
                "Encrypted reason scope or key version is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        EncryptedReasonEnvelope encryptedReason;
        try
        {
            encryptedReason = EncryptedReasonValidator.ToDomain(
                req.EncryptedReason,
                agent.SigningPublicKey,
                vault.AgentMessageKeyFingerprint!);
        }
        catch (Exception ex) when (ex is FormatException
                                   or Palladin.Core.Types.Exceptions.DomainException
                                   or CryptographicException)
        {
            AddError(request => request.EncryptedReason,
                "Encrypted reason envelope is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (await domainReadContext.Grants.AnyAsync(
                grant => grant.Id == req.EncryptedReason.GrantRequestId, ct))
        {
            AddError(request => request.EncryptedReason,
                "Grant request identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var names = await domainReadContext.ResolveAsync(
            agentId.Value,
            req.ScriptEntryId,
            req.VaultId,
            actorUserId: null,
            ct);
        var grant = ScriptExecutionGrant.RequestAccess(
            req.EncryptedReason.GrantRequestId,
            req.VaultId,
            agent.OrganizationId,
            agentId.Value,
            agent.PublicKey,
            req.ScriptEntryId,
            names,
            encryptedReason,
            clock.GetCurrentInstant(),
            accessEpoch.Value);

        if (await HasPendingDeprovisioningAsync(
                organizationId.Value, agentId.Value, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

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
            AddError(request => request.EncryptedReason,
                "Grant request identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Grants_ScriptExecution_Pending",
        })
        {
            domainWriteContext.Clear();
            var winner = await domainReadContext.Grants
                .OfType<ScriptExecutionGrant>()
                .Where(candidate => candidate.OrganizationId == agent.OrganizationId
                                    && candidate.AgentId == agentId.Value
                                    && candidate.AgentAccessEpoch == agent.AccessEpoch
                                    && candidate.VaultId == req.VaultId
                                    && candidate.ScriptEntryId == req.ScriptEntryId
                                    && candidate.Status == GrantStatus.Pending)
                .Select(candidate => new { candidate.Id, candidate.Status })
                .SingleOrDefaultAsync(ct);
            if (winner is not null)
            {
                await Send.OkAsync(new RequestAccessResponse(winner.Id, winner.Status), ct);
                return;
            }

            AddError(request => request.EncryptedReason,
                "A pending Script execution request already exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(new RequestAccessResponse(grant.Id, grant.Status), ct);
    }

    private Task<bool> HasPendingDeprovisioningAsync(
        Guid organizationId,
        Guid agentId,
        CancellationToken ct) =>
        domainReadContext.VaultPrincipalDeprovisionings.AnyAsync(candidate =>
            candidate.OrganizationId == organizationId
            && candidate.PrincipalType == VaultPrincipalType.Agent
            && candidate.PrincipalId == agentId
            && candidate.Status != VaultPrincipalDeprovisioningStatus.Completed, ct);
}
