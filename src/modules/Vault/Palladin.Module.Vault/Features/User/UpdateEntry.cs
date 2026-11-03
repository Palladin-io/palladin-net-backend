using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record UpdateEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string BaseRevision { get; init; } = string.Empty;
    public VaultEntryKeyContract? NewEntryKey { get; init; }
    public MemberSecretEnvelopeContract MemberSecret { get; init; } = null!;
    public required MemberIndexEnvelopeContract MemberIndex { get; init; }
    public bool AgentDiscoveryChanged { get; init; }
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public GrantDeliveryPolicy DeliveryPolicy { get; init; } = GrantDeliveryPolicy.Standard;
    public IReadOnlyList<GrantEntryEnvelopeContract> GrantEnvelopes { get; init; } = [];
    public IReadOnlyList<ScriptExecutionPackageContract> ScriptGrantPackages { get; init; } = [];
    public IReadOnlyList<Guid> RevokedScriptGrantIds { get; init; } = [];
}

[PublicAPI]
public sealed record UpdateEntryResponse(string CurrentRevision);

[UsedImplicitly]
internal sealed class UpdateEntryValidator : Validator<UpdateEntryRequest>
{
    public UpdateEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.BaseRevision).NotEmpty();
        RuleFor(x => x.MemberSecret).NotNull();
        RuleFor(x => x.MemberIndex).NotNull();
        RuleFor(x => x.DeliveryPolicy).Must(x => x.IsValid());
        RuleFor(x => x.AgentDiscovery).Null().When(x => !x.AgentDiscoveryChanged);
        RuleForEach(x => x.GrantEnvelopes).SetValidator(new GrantEntryEnvelopeContractValidator());
        RuleForEach(x => x.ScriptGrantPackages)
            .SetValidator(new ScriptExecutionPackageContractValidator());
        RuleFor(x => x.RevokedScriptGrantIds)
            .Must(ids => ids.Count <= 1000 && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count)
            .WithMessage("Revoked Script grant ids must be non-empty, unique, and within the request limit.");
    }
}

[PublicAPI]
internal sealed class UpdateEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateEntryRequest, UpdateEntryResponse>
{
    public override void Configure()
    {
        Put("api/vaults/{vaultId:guid}/entries/{entryId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Update a canonical encrypted Entry";
            summary.Description = "Creates one immutable canonical MemberSecret version and atomically switches every changed projection head using optimistic baseRevision concurrency. The backend never merges ciphertext.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(UpdateEntryRequest req, CancellationToken ct)
    {
        var staging = await EntryUpdate.StageAsync(
            domainWriteContext,
            req,
            User.GetOrganizationId()!.Value,
            User.GetUserId()!.Value,
            User.GetPermissions(),
            clock.GetCurrentInstant(),
            ct);
        if (staging.Failure is { } failure)
        {
            if (failure.StatusCode == StatusCodes.Status404NotFound)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            if (failure.StatusCode == StatusCodes.Status403Forbidden)
            {
                await Send.ForbiddenAsync(ct);
                return;
            }

            AddError(failure.Message);
            await Send.ErrorsAsync(failure.StatusCode, ct);
            return;
        }

        if (!staging.IsExactRetry)
        {
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.OkAsync(new UpdateEntryResponse(staging.Entry!.CurrentRevision.Value.ToString()), ct);
    }
}
