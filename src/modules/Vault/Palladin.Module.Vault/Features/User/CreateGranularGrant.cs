using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
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

        var staging = await GranularGrantCreation.StageAsync(
            domainWriteContext, req, organizationId, userId, clock.GetCurrentInstant(), ct);
        if (staging.Failure is { } failure)
        {
            if (failure.StatusCode == StatusCodes.Status404NotFound)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            AddError(failure.Message);
            await Send.ErrorsAsync(failure.StatusCode, ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (GranularGrantCreation.IsGrantIdentifierConflict(ex))
        {
            AddError(r => r.GrantId, "Grant identifier has already been used.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.CreatedAtAsync<GetGrantEndpoint>(
            new { vaultId = req.VaultId, grantId = staging.GrantId },
            new CreateGranularGrantResponse(staging.GrantId),
            cancellation: ct);
    }
}
