using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CreateScriptExecutionGrantRequest : IRequiresVaultMembership
{
    public Guid GrantId { get; init; }
    public Guid VaultId { get; init; }
    public Guid ScriptEntryId { get; init; }
    public Guid AgentId { get; init; }
    public ScriptExecutionPackageContract ScriptPackage { get; init; } = null!;
    public Instant? ExpiresAt { get; init; }
    public int? QueryLimit { get; init; }
    public GrantMethods Methods { get; init; } = GrantMethods.Exec;
}

[PublicAPI]
public sealed record CreateScriptExecutionGrantResponse(Guid Id);

[UsedImplicitly]
internal sealed class CreateScriptExecutionGrantValidator : Validator<CreateScriptExecutionGrantRequest>
{
    public CreateScriptExecutionGrantValidator()
    {
        RuleFor(request => request.GrantId).NotEmpty();
        RuleFor(request => request.VaultId).NotEmpty();
        RuleFor(request => request.ScriptEntryId).NotEmpty();
        RuleFor(request => request.AgentId).NotEmpty();
        RuleFor(request => request.Methods).Equal(GrantMethods.Exec);
        RuleFor(request => request).Must(request => !(request.ExpiresAt.HasValue && request.QueryLimit.HasValue));
        RuleFor(request => request.ExpiresAt)
            .Must(value => !value.HasValue || PostgreSqlInstant.Normalize(value.Value) == value.Value);
        RuleFor(request => request.QueryLimit!.Value).GreaterThan(0).When(request => request.QueryLimit.HasValue);
        RuleFor(request => request.ScriptPackage)
            .NotNull()
            .SetValidator(new ScriptExecutionPackageContractValidator());
        RuleFor(request => request)
            .Must(request => request.ScriptPackage is not null
                             && request.ScriptPackage.ScriptEntryId == request.ScriptEntryId)
            .WithMessage("Script package must target the route Script Entry.");
    }
}

[PublicAPI]
internal sealed class CreateScriptExecutionGrantEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateScriptExecutionGrantRequest, CreateScriptExecutionGrantResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/scripts/{scriptEntryId:guid}/grants");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a Script execution grant proactively";
            summary.Description = "Creates one Active ScriptExecution grant containing the complete encrypted Script package and all revision-bound referenced Entry scopes. The server stores ciphertext only.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(CreateScriptExecutionGrantRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        var agent = await domainWriteContext.Agents.SingleOrDefaultAsync(
            candidate => candidate.Id == req.AgentId && candidate.OrganizationId == organizationId,
            ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (agent.Status != AgentStatus.Active)
        {
            AddError(request => request.AgentId, "Agent is not active.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (await domainReadContext.VaultPrincipalDeprovisionings.AnyAsync(operation =>
                operation.OrganizationId == organizationId
                && operation.PrincipalType == VaultPrincipalType.Agent
                && operation.PrincipalId == req.AgentId
                && operation.Status != VaultPrincipalDeprovisioningStatus.Completed,
                ct))
        {
            AddError(request => request.AgentId, "Agent deactivation is in progress.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var vault = await domainWriteContext.Vaults.SingleOrDefaultAsync(
            candidate => candidate.OrganizationId == organizationId && candidate.Id == req.VaultId,
            ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var scopeEntryIds = req.ScriptPackage.Scopes.Select(scope => scope.EntryId).ToArray();
        var entries = await domainWriteContext.Entries
            .Where(entry => entry.OrganizationId == organizationId
                            && entry.VaultId == req.VaultId
                            && scopeEntryIds.Contains(entry.Id))
            .ToListAsync(ct);
        if (entries.Count != scopeEntryIds.Length || entries.Any(entry => entry.State != EntryState.Active))
        {
            AddError(request => request.ScriptPackage, "One or more Script package entries are no longer active.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var scriptEntry = entries.Single(entry => entry.Id == req.ScriptEntryId);
        if (scriptEntry.DeliveryPolicy != GrantDeliveryPolicy.ExecOnly)
        {
            AddError(request => request.ScriptEntryId, "Script Entry must use the exec-only delivery policy.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var hasActiveCoverage = await domainReadContext.Grants.AnyAsync(grant =>
            grant.AgentId == req.AgentId
            && grant.AgentAccessEpoch == agent.AccessEpoch
            && grant.VaultId == req.VaultId
            && grant.Status == GrantStatus.Active
            && ((grant is FullGrant
                 && (grant.Methods & GrantMethods.Exec) == GrantMethods.Exec
                 && grant.AgentWrappedVaultKey != null)
                || (grant is ScriptExecutionGrant
                    && ((ScriptExecutionGrant)grant).ScriptEntryId == req.ScriptEntryId
                    && grant.ScriptExecutionPackage != null)),
            ct);
        if (hasActiveCoverage)
        {
            throw new AgentAlreadyHasActiveAccessException("script");
        }

        ScriptExecutionPackage package;
        IReadOnlyList<ScriptExecutionScope> scopes;
        try
        {
            (package, scopes) = ScriptExecutionPackageContractMapper.ToDomain(req.ScriptPackage);
            ScriptExecutionGrantMaterialValidator.Validate(
                package,
                scopes,
                organizationId,
                req.VaultId,
                req.GrantId,
                req.AgentId,
                agent.AccessEpoch,
                req.ScriptEntryId,
                agent.PublicKey,
                agent.RecipientKeyVersion,
                entries.ToDictionary(entry => entry.Id, entry => entry.CurrentRevision.Value));
            ScriptExecutionPackageCryptoValidator.ValidateProducer(req.ScriptPackage, vault);
        }
        catch (Exception exception) when (exception is FormatException
            or Palladin.Core.Types.Exceptions.DomainException)
        {
            AddError(request => request.ScriptPackage, "Script execution package is invalid or stale.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var names = await domainReadContext.ResolveAsync(
            req.AgentId, req.ScriptEntryId, req.VaultId, userId, ct);
        var grant = ScriptExecutionGrant.CreateProactively(
            req.GrantId,
            req.VaultId,
            organizationId,
            req.AgentId,
            agent.PublicKey,
            req.ScriptEntryId,
            scopes,
            package,
            req.ExpiresAt,
            req.QueryLimit,
            ExpirySource.From(req.ExpiresAt, req.QueryLimit),
            userId,
            names,
            now,
            agent.AccessEpoch);

        domainWriteContext.Add(grant);
        agent.FenceAccessMutation();
        vault.FenceAccessMutation(userId, now);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_Grants",
        })
        {
            AddError(request => request.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Grants_OrganizationId_VaultId_ScriptEntryId_AgentId_AgentAc~",
        })
        {
            AddError(request => request.AgentId, "Agent already has active direct access to this Script.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = grant.Id },
            new CreateScriptExecutionGrantResponse(grant.Id),
            cancellation: ct);
    }
}
