using FastEndpoints;
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
public sealed record CreateEntryRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public VaultEntryKeyContract EntryKey { get; init; } = null!;
    public MemberIndexEnvelopeContract MemberIndex { get; init; } = null!;
    public MemberSecretEnvelopeContract MemberSecret { get; init; } = null!;
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public GrantDeliveryPolicy DeliveryPolicy { get; init; } = GrantDeliveryPolicy.Standard;
}

[PublicAPI]
public sealed record CreateEntryResponse(Guid Id, string CurrentRevision);

[UsedImplicitly]
internal sealed class CreateEntryValidator : Validator<CreateEntryRequest>
{
    public CreateEntryValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.EntryKey).NotNull();
        RuleFor(x => x.MemberIndex).NotNull();
        RuleFor(x => x.MemberSecret).NotNull();
        RuleFor(x => x.DeliveryPolicy).Must(x => x.IsValid());
    }
}

[PublicAPI]
internal sealed class CreateEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateEntryRequest, CreateEntryResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Create a canonical encrypted Entry";
            summary.Description = "Atomically stores the Entry head, wrapped EntryDEK, canonical MemberSecret version, MemberIndex projection and optional AgentDiscovery projection. All presentation fields remain encrypted on the client.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(CreateEntryRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var material = EntryCreationMaterial.From(req);

        var existing = await EntryCreation.FindAsync(domainWriteContext, organizationId, req.VaultId, req.EntryId, ct);
        if (existing is not null)
        {
            if (!existing.IsExactCreateRetry(material.EntryKey, material.MemberSecret, material.MemberIndex,
                    material.AgentDiscovery, req.DeliveryPolicy))
            {
                ThrowError("Entry identifier is already bound to a different canonical transition.");
            }

            await Send.OkAsync(new CreateEntryResponse(existing.Id, existing.CurrentRevision.Value.ToString()), ct);
            return;
        }

        var entry = await EntryCreation.StageAsync(
            domainWriteContext, req, material, organizationId, userId, clock.GetCurrentInstant(), ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await domainWriteContext.CommitAsync(ct);

        await Send.CreatedAtAsync<GetEntryEndpoint>(
            new { vaultId = req.VaultId, entryId = entry.Id },
            new CreateEntryResponse(entry.Id, entry.CurrentRevision.Value.ToString()),
            cancellation: ct);
    }
}
