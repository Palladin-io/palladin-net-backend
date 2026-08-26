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
public sealed record CreateFullGrantRequest : IRequiresVaultMembership
{
    public Guid GrantId { get; init; }
    public Guid VaultId { get; init; }
    public Guid AgentId { get; init; }
    public AgentWrappedVaultKeyContract AgentWrappedVaultKey { get; init; } = null!;
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }
    public GrantMethods Methods { get; init; } = GrantMethods.Get;
}

[PublicAPI]
public sealed record CreateFullGrantResponse(Guid Id);

[UsedImplicitly]
internal sealed class CreateFullGrantValidator : Validator<CreateFullGrantRequest>
{
    public CreateFullGrantValidator()
    {
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.Methods).Must(methods => methods.IsValidSet());
        RuleFor(x => x).Must(x => !(x.ExpiresAt.HasValue && x.QueryLimit.HasValue));
        RuleFor(x => x.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(x => x.QueryLimit!.Value).GreaterThan(0).When(x => x.QueryLimit.HasValue);
        RuleFor(x => x.AgentWrappedVaultKey)
            .NotNull()
            .SetValidator(new AgentWrappedVaultKeyContractValidator());
    }
}

[PublicAPI]
internal sealed class CreateFullGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateFullGrantRequest, CreateFullGrantResponse>
{
    private const int SupersedePageSize = 100;

    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/full-grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a full-vault grant proactively";
            summary.Description = "Creates an Active FULL grant using one current Vault key sealed to the authoritative Agent key and atomically supersedes the Agent's active per-entry grants in this Vault. The server stores ciphertext only.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CreateFullGrantRequest req, CancellationToken ct)
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

        if (await domainReadContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
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
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await domainReadContext.HasActiveFullCoverageAsync(
                req.AgentId, agent.AccessEpoch, req.VaultId, ct))
        {
            throw new AgentAlreadyHasActiveAccessException("vault");
        }

        if (await domainReadContext.Grants.AnyAsync(grant => grant.Id == req.GrantId, ct))
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var expirySource = ExpirySource.From(req.ExpiresAt, req.QueryLimit);
        var names = await domainReadContext.ResolveAsync(req.AgentId, null, req.VaultId, userId, ct);
        AgentWrappedVaultKey wrappedVaultKey;
        try
        {
            var fingerprint = VaultKeyFingerprint.Compute(
                Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519);
            wrappedVaultKey = AgentWrappedVaultKeyContractMapper.ToDomain(
                req.AgentWrappedVaultKey, organizationId, req.VaultId, req.GrantId, req.AgentId,
                agent.AccessEpoch, vault.CurrentVaultKeyVersion.Value, agent.RecipientKeyVersion,
                fingerprint);
        }
        catch (Exception ex) when (ex is FormatException
            or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(r => r.AgentWrappedVaultKey, "Agent Vault-key wrapper is invalid or stale.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var grant = FullGrant.CreateProactively(
            req.GrantId, req.VaultId, organizationId, req.AgentId, agent.PublicKey,
            wrappedVaultKey, req.ExpiresAt, req.QueryLimit, expirySource, req.Methods,
            userId, names, now, agent.AccessEpoch);

        // This transaction owns one security invariant: no committed state may contain the new FULL
        // grant while old active GRANULAR delivery material is still usable. The work is paged and
        // domain-driven so every superseded Grant emits its own outbox-backed lifecycle events.
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var expectedAccessEpoch = agent.AccessEpoch;
        var expectedPublicKey = agent.PublicKey;
        var expectedRecipientKeyVersion = agent.RecipientKeyVersion;
        var expectedVaultKeyVersion = vault.CurrentVaultKeyVersion;
        var expectedMemberKeyGeneration = vault.MemberKeyGeneration;

        var stableAgent = await domainWriteContext.LockAgent(organizationId, req.AgentId).SingleAsync(ct);
        var stableVault = await domainWriteContext.LockVault(organizationId, req.VaultId).SingleAsync(ct);
        if (stableAgent.Status != AgentStatus.Active
            || stableAgent.AccessEpoch != expectedAccessEpoch
            || stableAgent.PublicKey != expectedPublicKey
            || stableAgent.RecipientKeyVersion != expectedRecipientKeyVersion
            || stableVault.CurrentVaultKeyVersion != expectedVaultKeyVersion
            || stableVault.MemberKeyGeneration != expectedMemberKeyGeneration)
        {
            await transaction.RollbackAsync(ct);
            AddError(r => r.AgentWrappedVaultKey, "Agent or Vault key context changed during grant creation.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        domainWriteContext.Clear();
        Guid? afterGrantId = null;
        while (true)
        {
            var page = await domainWriteContext.LoadActiveGranularInVaultPageAsync(
                req.AgentId, expectedAccessEpoch, req.VaultId, afterGrantId, SupersedePageSize, ct);
            if (page.Count == 0)
            {
                break;
            }

            domainWriteContext.EnsureFullGrantCommitTrackingIsBounded(SupersedePageSize);
            var entryIds = page.Select(granular => granular.EntryId).Distinct().ToArray();
            var namesByEntry = await domainReadContext.ResolveForSupersedeAsync(
                req.AgentId, req.VaultId, entryIds, ct);
            foreach (var granular in page)
            {
                granular.SupersedeByFull(grant.Id, namesByEntry[granular.EntryId], now);
            }

            afterGrantId = page[^1].Id;
            await domainWriteContext.FlushAsync(ct);
            domainWriteContext.Clear();
        }

        agent = await domainWriteContext.Agents.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == req.AgentId, ct);
        vault = await domainWriteContext.Vaults.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        domainWriteContext.Add(grant);
        agent.FenceAccessMutation();
        vault.FenceAccessMutation(userId, now);

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

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = grant.Id },
            new CreateFullGrantResponse(grant.Id),
            cancellation: ct);
    }
}
