using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record StartFullGrantPreparationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
    public Guid AgentId { get; init; }
    public GrantMethods Methods { get; init; } = GrantMethods.Get;
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }
}

[PublicAPI]
public sealed record StartFullGrantPreparationResponse(
    Guid OrganizationId,
    Guid GrantId,
    Instant PreparationExpiresAt,
    uint MemberKeyGeneration,
    uint AgentAccessEpoch,
    uint RecipientAgentKeyVersion,
    string AgentKeyFingerprint,
    string AgentPublicKey);

[UsedImplicitly]
internal sealed class StartFullGrantPreparationValidator : Validator<StartFullGrantPreparationRequest>
{
    public StartFullGrantPreparationValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.Methods).Must(x => x.IsValidSet());
        RuleFor(x => x).Must(x => !(x.ExpiresAt.HasValue && x.QueryLimit.HasValue));
        RuleFor(x => x.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(x => x.QueryLimit!.Value).GreaterThan(0).When(x => x.QueryLimit.HasValue);
    }
}

[PublicAPI]
internal sealed class StartFullGrantPreparationEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IOptions<VaultCryptoOptions> cryptoOptions,
    IClock clock) : Endpoint<StartFullGrantPreparationRequest, StartFullGrantPreparationResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/grants/full/preparations");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Start a bounded FULL grant preparation";
            summary.Description = "Pins the server-owned Vault and Agent key context for a resumable client-side envelope preparation. No grant becomes active before commit.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(StartFullGrantPreparationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        await domainWriteContext.LockOrganizationAgentLifecycle(organizationId).SingleAsync(ct);
        var vault = await domainWriteContext.LockVault(organizationId, req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var agent = await domainWriteContext.LockAgent(organizationId, req.AgentId)
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active
            || await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.Agent
                && x.PrincipalId == req.AgentId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed, ct))
        {
            AddError(r => r.AgentId, "Agent is not active.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (await domainWriteContext.Grants.OfType<FullGrant>().AnyAsync(g =>
                g.OrganizationId == organizationId
                && g.VaultId == req.VaultId
                && g.AgentId == req.AgentId
                && g.AgentAccessEpoch == agent.AccessEpoch
                && g.Status == GrantStatus.Active, ct))
        {
            throw new AgentAlreadyHasActiveAccessException("vault");
        }

        if (await domainWriteContext.Grants.AnyAsync(g => g.Id == req.GrantId, ct))
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var existingById = await domainWriteContext.FullGrantPreparations
            .SingleOrDefaultAsync(x => x.Id == req.GrantId, ct);
        if (existingById is not null
            && (existingById.OrganizationId != organizationId
                || existingById.VaultId != req.VaultId
                || existingById.AgentId != req.AgentId))
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var existing = existingById ?? await domainWriteContext.FullGrantPreparations
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId
                                       && x.VaultId == req.VaultId
                                       && x.AgentId == req.AgentId, ct);
        if (existing is not null && now >= existing.PreparationExpiresAt)
        {
            domainWriteContext.Remove(existing);
            await domainWriteContext.FlushAsync(ct);
            domainWriteContext.Clear();
            existing = null;
        }

        if (existing is not null)
        {
            if (existing.Id != req.GrantId
                || !existing.IsExactRetry(req.AgentId, req.Methods, req.ExpiresAt, req.QueryLimit, userId))
            {
                AddError(r => r.GrantId, "A different FULL grant preparation is already active for this Agent and Vault.");
                await Send.ErrorsAsync(409, ct);
                return;
            }

            await Send.OkAsync(ToResponse(existing), ct);
            return;
        }

        var preparation = FullGrantPreparation.Create(
            organizationId,
            req.VaultId,
            req.GrantId,
            req.AgentId,
            agent.AccessEpoch,
            agent.PublicKey,
            agent.RecipientKeyVersion,
            VaultKeyFingerprint.Compute(Convert.FromBase64String(agent.PublicKey), VaultKeyKind.AgentX25519),
            vault.MemberKeyGeneration.Value,
            req.Methods,
            req.ExpiresAt,
            req.QueryLimit,
            ExpirySource.From(req.ExpiresAt, req.QueryLimit),
            userId,
            now,
            now.Plus(Duration.FromMinutes(cryptoOptions.Value.FullGrantPreparationTtlMinutes)));
        domainWriteContext.Add(preparation);
        try
        {
            await domainWriteContext.CommitAsync(transaction, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_FullGrantPreparations_Id",
        })
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.CreatedAtAsync<GetFullGrantPreparationMaterialEndpoint>(
            new { vaultId = req.VaultId, grantId = req.GrantId },
            ToResponse(preparation),
            cancellation: ct);
    }

    private static StartFullGrantPreparationResponse ToResponse(FullGrantPreparation preparation) => new(
        preparation.OrganizationId,
        preparation.Id,
        preparation.PreparationExpiresAt,
        preparation.MemberKeyGeneration,
        preparation.AgentAccessEpoch,
        preparation.RecipientAgentKeyVersion,
        WebEncoders.Base64UrlEncode(preparation.AgentKeyFingerprint),
        preparation.AgentPublicKey);
}
