using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ImportEntryItem
{
    public Guid EntryId { get; init; }
    public VaultEntryKeyContract EntryKey { get; init; } = null!;
    public MemberIndexEnvelopeContract MemberIndex { get; init; } = null!;
    public MemberSecretEnvelopeContract MemberSecret { get; init; } = null!;
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
    public GrantDeliveryPolicy DeliveryPolicy { get; init; } = GrantDeliveryPolicy.Standard;
}

[PublicAPI]
public sealed record ImportEntriesRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public string Format { get; init; } = string.Empty;
    public IReadOnlyList<ImportEntryItem> Entries { get; init; } = [];
}

[PublicAPI]
public sealed record ImportEntriesResponse(int ImportedCount, IReadOnlyList<Guid> EntryIds);

[UsedImplicitly]
internal sealed class ImportEntriesValidator : Validator<ImportEntriesRequest>
{
    private const int MaxBatchSize = 500;

    public ImportEntriesValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.Format).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Entries).NotEmpty();
        RuleFor(x => x.Entries.Count).InclusiveBetween(1, MaxBatchSize).When(x => x.Entries is not null);
        RuleFor(x => x.Entries.Select(entry => entry.EntryId).Distinct().Count())
            .Equal(x => x.Entries.Count);
        RuleForEach(x => x.Entries).ChildRules(item =>
        {
            item.RuleFor(x => x.EntryId).NotEmpty();
            item.RuleFor(x => x.EntryKey).NotNull();
            item.RuleFor(x => x.MemberIndex).NotNull();
            item.RuleFor(x => x.MemberSecret).NotNull();
            item.RuleFor(x => x.DeliveryPolicy).Must(x => x.IsValid());
        });
    }
}

[PublicAPI]
internal sealed class ImportEntriesEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<ImportEntriesRequest, ImportEntriesResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/import");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Import canonical encrypted Entries";
            summary.Description = "Atomically stores a bounded batch of complete encrypted Entry transitions. Plaintext import data is normalized and encrypted by the client before this request.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(ImportEntriesRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();

        var requestedEntryIds = req.Entries.Select(item => item.EntryId).ToList();
        var transitions = req.Entries.Select(item => new
        {
            Item = item,
            EntryKey = VaultEnvelopeContractMapper.ToDomain(item.EntryKey),
            MemberIndex = VaultEnvelopeContractMapper.ToDomain(item.MemberIndex),
            MemberSecret = VaultEnvelopeContractMapper.ToDomain(item.MemberSecret),
            AgentDiscovery = item.AgentDiscovery is null
                ? null
                : VaultEnvelopeContractMapper.ToDomain(item.AgentDiscovery),
        }).ToList();
        var existing = await domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && requestedEntryIds.Contains(x.Id))
            .ToListAsync(ct);
        var existingById = existing.ToDictionary(x => x.Id);
        foreach (var transition in transitions.Where(x => existingById.ContainsKey(x.Item.EntryId)))
        {
            if (!existingById[transition.Item.EntryId].IsExactCreateRetry(
                    transition.EntryKey,
                    transition.MemberSecret,
                    transition.MemberIndex,
                    transition.AgentDiscovery,
                    transition.Item.DeliveryPolicy))
            {
                ThrowError("An imported Entry identifier is already bound to a different canonical transition.");
            }
        }

        var missing = transitions.Where(x => !existingById.ContainsKey(x.Item.EntryId)).ToList();
        var missingIds = missing.Select(x => x.Item.EntryId).ToList();
        var lockedVault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .SingleOrDefaultAsync(ct);
        if (lockedVault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var challenges = await domainWriteContext.EntryCreationChallenges
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == req.VaultId
                        && x.RequestedBy == userId
                        && missingIds.Contains(x.EntryId))
            .ToListAsync(ct);
        if (challenges.Count != missingIds.Count)
        {
            ThrowError("Every imported Entry identifier requires a server-issued creation challenge.");
        }

        foreach (var challenge in challenges)
        {
            challenge.Consume(new EntryScope(organizationId, req.VaultId, challenge.EntryId), userId, now);
        }

        var entries = missing.Select(transition =>
        {
            var sequences = lockedVault.AllocateSequences(transition.AgentDiscovery is not null, userId, now);
            var version = VaultEntryVersion.Create(
                transition.MemberSecret,
                sequences,
                memberIndexChanged: true,
                now,
                ActorType.Member,
                userId);
            return VaultEntry.Create(
                new EntryScope(lockedVault.OrganizationId, lockedVault.Id, transition.Item.EntryId),
                transition.EntryKey,
                version,
                transition.MemberIndex,
                transition.AgentDiscovery,
                transition.Item.DeliveryPolicy,
                lockedVault.MemberKeyGeneration,
                lockedVault.CurrentVaultKeyVersion,
                lockedVault.CurrentVdkVersion,
                now,
                userId,
                true);
        }).ToList();

        if (entries.Count > 0)
        {
            domainWriteContext.AddRange(entries);
            await domainWriteContext.CommitAsync(ct);
        }

        foreach (var publisher in eventPublishers.Where(_ => entries.Count > 0))
        {
            await publisher.PublishAsync(new EntriesImportedEvent(
                req.VaultId,
                userId,
                entries.Count,
                req.Format,
                entries.Select(x => x.Id).ToList(),
                now), ct);
        }

        await Send.OkAsync(new ImportEntriesResponse(req.Entries.Count, requestedEntryIds), ct);
    }
}
