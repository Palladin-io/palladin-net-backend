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
public sealed record CreateGranularGrantRequest : IRequiresVaultMembership
{
    public Guid GrantId { get; init; }
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public Guid AgentId { get; init; }
    public GrantEntryEnvelopeContract GrantEntry { get; init; } = null!;
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }
    public GrantFieldSelectionMode FieldSelectionMode { get; init; } = GrantFieldSelectionMode.All;
    public GrantMethods Methods { get; init; } = GrantMethods.Get;
}

[PublicAPI]
public sealed record CreateGranularGrantResponse(Guid Id);

[UsedImplicitly]
internal sealed class CreateGranularGrantValidator : Validator<CreateGranularGrantRequest>
{
    public CreateGranularGrantValidator()
    {
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.FieldSelectionMode).IsInEnum();
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.Methods).Must(methods => methods.IsValidSet());
        RuleFor(x => x).Must(x => !(x.ExpiresAt.HasValue && x.QueryLimit.HasValue));
        RuleFor(x => x.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(x => x.QueryLimit!.Value).GreaterThan(0).When(x => x.QueryLimit.HasValue);
        RuleFor(x => x.GrantEntry)
            .NotNull()
            .SetValidator(new GrantEntryEnvelopeContractValidator());
        RuleFor(x => x)
            .Must(x => x.GrantEntry is not null && x.GrantEntry.EntryId == x.EntryId)
            .WithMessage("Grant envelope must target the route entry.");
    }
}

[PublicAPI]
internal sealed class CreateGranularGrantEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateGranularGrantRequest, CreateGranularGrantResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/{entryId:guid}/grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a per-entry grant proactively";
            summary.Description = "Creates or approves an Active GRANULAR grant for exactly one Entry. The client supplies the revision-bound encrypted Entry envelope; the server stores ciphertext only.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CreateGranularGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var agent = await domainWriteContext.Agents
            .SingleOrDefaultAsync(a => a.Id == req.AgentId && a.OrganizationId == organizationId, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active)
        {
            AddError(r => r.AgentId, "Agent is not active.");
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

        var vault = await domainWriteContext.Vaults.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        var entry = await domainWriteContext.Entries.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.EntryId, ct);
        if (vault is null || entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entry.State != EntryState.Active)
        {
            AddError(r => r.EntryId, "Entry is no longer active.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (await domainWriteContext.HasActiveEntryCoverageAsync(
                req.AgentId, agent.AccessEpoch, req.VaultId, req.EntryId, excludingGrantId: null, ct))
        {
            throw new AgentAlreadyHasActiveAccessException("entry");
        }

        var now = clock.GetCurrentInstant();
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
            AddError(r => r.GrantEntry, "Grant envelope is invalid or stale.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (Exception ex) when (ex is FormatException
            or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(r => r.GrantEntry, "Grant envelope is invalid or stale.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var pending = await domainWriteContext.FindPendingForEntryAsync(
            req.AgentId, agent.AccessEpoch, req.VaultId, req.EntryId, ct);
        if (pending is not null)
        {
            if (req.GrantId != pending.Id)
            {
                AddError(r => r.GrantId, "Grant envelope must target the existing pending request.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            pending.Approve(userId, names, scope, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods, now);
            agent.FenceAccessMutation();
            vault.FenceAccessMutation(userId, now);
            await domainWriteContext.CommitAsync(ct);
            await Send.CreatedAtAsync<GetGrantEndpoint>(
                new { vaultId = req.VaultId, grantId = pending.Id },
                new CreateGranularGrantResponse(pending.Id),
                cancellation: ct);
            return;
        }

        var grant = GranularGrant.CreateProactively(
            req.GrantId, req.VaultId, organizationId, req.AgentId, agent.PublicKey, req.EntryId,
            scope, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods, userId, names, now,
            agent.AccessEpoch);
        domainWriteContext.Add(grant);
        agent.FenceAccessMutation();
        vault.FenceAccessMutation(userId, now);
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
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = grant.Id },
            new CreateGranularGrantResponse(grant.Id),
            cancellation: ct);
    }

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
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant envelope scope or lifecycle is invalid.");
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
            throw new Palladin.Core.Types.Exceptions.DomainException("Grant envelope Agent key fingerprint is invalid.");
        }
        scope.SetFieldSelectionMode(req.FieldSelectionMode);
        return scope;
    }

    private sealed class StaleGrantEnvelopeException()
        : Palladin.Core.Types.Exceptions.ConflictException("Grant envelope entry revision is stale.");
}
