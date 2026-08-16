using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ChangeEntryStateRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public string BaseRevision { get; init; } = string.Empty;
    public VaultEntryKeyContract? NewEntryKey { get; init; }
    public required MemberSecretEnvelopeContract MemberSecret { get; init; }
    public MemberIndexEnvelopeContract? MemberIndex { get; init; }
    public AgentDiscoveryEnvelopeContract? AgentDiscovery { get; init; }
}

[PublicAPI]
public sealed record ChangeEntryStateResponse(EntryState State, string CurrentRevision);

[UsedImplicitly]
internal sealed class ChangeEntryStateValidator : Validator<ChangeEntryStateRequest>
{
    public ChangeEntryStateValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.BaseRevision).NotEmpty();
        RuleFor(x => x.MemberSecret).NotNull();
    }
}

internal enum EntryLifecycleMutation
{
    NotFound,
    Retried,
    Changed,
}

[UsedImplicitly]
internal sealed class EntryLifecycleService(
    VaultDomainWriteContext domainWriteContext,
    IOptions<VaultEntryLifecycleOptions> lifecycleOptions,
    IClock clock)
{
    internal async Task<(EntryLifecycleMutation Result, VaultEntry? Entry)> ArchiveAsync(
        Guid organizationId,
        Guid userId,
        ChangeEntryStateRequest request,
        CancellationToken cancellationToken)
    {
        var transition = Map(request);
        var entry = await LoadEntryAsync(organizationId, request, cancellationToken);
        if (entry is null)
        {
            return (EntryLifecycleMutation.NotFound, null);
        }

        if (entry.IsExactLifecycleRetry(
                transition.BaseRevision,
                EntryState.Archived,
                transition.NewKey,
                transition.MemberSecret,
                transition.MemberIndex,
                null))
        {
            return (EntryLifecycleMutation.Retried, entry);
        }

        var now = clock.GetCurrentInstant();
        var vault = await LoadVaultAsync(entry, cancellationToken);
        var sequences = vault.AllocateSequences(entry.GetAgentDiscovery() is not null, userId, now);
        entry.Archive(
            transition.BaseRevision,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            transition.NewKey,
            transition.MemberSecret,
            transition.MemberIndex,
            sequences,
            now,
            userId);
        await domainWriteContext.CommitAsync(cancellationToken);
        return (EntryLifecycleMutation.Changed, entry);
    }

    internal async Task<(EntryLifecycleMutation Result, VaultEntry? Entry)> DeleteAsync(
        Guid organizationId,
        Guid userId,
        ChangeEntryStateRequest request,
        CancellationToken cancellationToken)
    {
        var transition = Map(request);
        var vault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == request.VaultId)
            .SingleOrDefaultAsync(cancellationToken);
        if (vault is null)
        {
            return (EntryLifecycleMutation.NotFound, null);
        }

        var entry = await domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId
                                       && x.VaultId == request.VaultId
                                       && x.Id == request.EntryId,
                cancellationToken);
        if (entry is null)
        {
            return (EntryLifecycleMutation.NotFound, null);
        }

        if (entry.IsExactLifecycleRetry(
                transition.BaseRevision,
                EntryState.Deleted,
                transition.NewKey,
                transition.MemberSecret,
                transition.MemberIndex,
                null))
        {
            return (EntryLifecycleMutation.Retried, entry);
        }

        var now = clock.GetCurrentInstant();
        var grants = await domainWriteContext.Grants
            .Include(x => x.EncryptedReason)
            .Include(x => x.GrantEntryScopes)
            .ThenInclude(scope => scope.Envelope)
            .Where(x => x.OrganizationId == organizationId
                        && x.VaultId == request.VaultId
                        && (x.GrantEntryScopes.Any(scope => scope.EntryId == request.EntryId)
                            || (x is GranularGrant && ((GranularGrant)x).EntryId == request.EntryId)))
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RemoveEntryAccess(request.EntryId, now);
        }

        var sequences = vault.AllocateSequences(entry.GetAgentDiscovery() is not null, userId, now);
        entry.Delete(
            transition.BaseRevision,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            transition.NewKey,
            transition.MemberSecret,
            transition.MemberIndex,
            sequences,
            now,
            userId);
        await domainWriteContext.CommitAsync(cancellationToken);
        return (EntryLifecycleMutation.Changed, entry);
    }

    internal async Task<(EntryLifecycleMutation Result, VaultEntry? Entry)> RestoreAsync(
        Guid organizationId,
        Guid userId,
        ChangeEntryStateRequest request,
        CancellationToken cancellationToken)
    {
        var transition = Map(request);
        var entry = await LoadEntryAsync(organizationId, request, cancellationToken);
        if (entry is null)
        {
            return (EntryLifecycleMutation.NotFound, null);
        }

        if (entry.IsExactLifecycleRetry(
                transition.BaseRevision,
                EntryState.Active,
                transition.NewKey,
                transition.MemberSecret,
                transition.MemberIndex,
                transition.AgentDiscovery))
        {
            return (EntryLifecycleMutation.Retried, entry);
        }

        var now = clock.GetCurrentInstant();
        var vault = await LoadVaultAsync(entry, cancellationToken);
        var sequences = vault.AllocateSequences(transition.AgentDiscovery is not null, userId, now);
        entry.Restore(
            transition.BaseRevision,
            vault.MemberKeyGeneration,
            vault.CurrentVaultKeyVersion,
            vault.CurrentVdkVersion,
            transition.NewKey,
            transition.MemberSecret,
            transition.MemberIndex,
            transition.AgentDiscovery,
            sequences,
            Duration.FromDays(lifecycleOptions.Value.RecentlyDeletedDays),
            now,
            userId);
        await domainWriteContext.CommitAsync(cancellationToken);
        return (EntryLifecycleMutation.Changed, entry);
    }

    private Task<VaultEntry?> LoadEntryAsync(
        Guid organizationId,
        ChangeEntryStateRequest request,
        CancellationToken cancellationToken) =>
        domainWriteContext.Entries
            .Include(x => x.Keys)
            .Include(x => x.Versions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.VaultId == request.VaultId
                                      && x.Id == request.EntryId,
                cancellationToken);

    private Task<Domain.Vault> LoadVaultAsync(VaultEntry entry, CancellationToken cancellationToken) =>
        domainWriteContext.Vaults.FirstAsync(
            x => x.OrganizationId == entry.OrganizationId && x.Id == entry.VaultId,
            cancellationToken);

    private static EntryLifecycleTransition Map(ChangeEntryStateRequest request) => new(
        VaultEnvelopeContractMapper.ToEntryRevision(request.BaseRevision),
        request.NewEntryKey is null ? null : VaultEnvelopeContractMapper.ToDomain(request.NewEntryKey),
        VaultEnvelopeContractMapper.ToDomain(request.MemberSecret),
        request.MemberIndex is null ? null : VaultEnvelopeContractMapper.ToDomain(request.MemberIndex),
        request.AgentDiscovery is null ? null : VaultEnvelopeContractMapper.ToDomain(request.AgentDiscovery));

    private sealed record EntryLifecycleTransition(
        EntryRevision BaseRevision,
        VaultEntryKey? NewKey,
        MemberSecretCiphertext MemberSecret,
        MemberIndexCiphertext? MemberIndex,
        AgentDiscoveryCiphertext? AgentDiscovery);
}

internal abstract class ChangeEntryStateEndpointBase(
    EntryLifecycleService lifecycleService) : Endpoint<ChangeEntryStateRequest, ChangeEntryStateResponse>
{
    protected EntryLifecycleService LifecycleService { get; } = lifecycleService;

    protected void ConfigureMemberMutation(string route, string summary, string description)
    {
        Post(route);
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(config =>
        {
            config.Summary = summary;
            config.Description = description;
        });
        Tags("Vault/Entries");
    }

    protected async Task SendResultAsync(
        (EntryLifecycleMutation Result, VaultEntry? Entry) result,
        CancellationToken cancellationToken)
    {
        if (result.Result == EntryLifecycleMutation.NotFound)
        {
            await Send.NotFoundAsync(cancellationToken);
            return;
        }

        var entry = result.Entry!;
        await Send.OkAsync(new ChangeEntryStateResponse(
            entry.State,
            entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture)), cancellationToken);
    }
}

[PublicAPI]
internal sealed class ArchiveEntryEndpoint(EntryLifecycleService lifecycleService)
    : ChangeEntryStateEndpointBase(lifecycleService)
{
    public override void Configure() => ConfigureMemberMutation(
        "api/vaults/{vaultId:guid}/entries/{entryId:guid}/archive",
        "Archive an encrypted Entry",
        "Moves an Active Entry to Archive through a new immutable canonical revision and removes it from Agent Discovery.");

    public override async Task HandleAsync(ChangeEntryStateRequest req, CancellationToken ct)
    {
        if (req.AgentDiscovery is not null)
        {
            AddError("Archive cannot publish Agent Discovery ciphertext.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await SendResultAsync(await LifecycleService.ArchiveAsync(
            User.GetOrganizationId()!.Value,
            User.GetUserId()!.Value,
            req,
            ct), ct);
    }
}

[PublicAPI]
internal sealed class RestoreEntryEndpoint(EntryLifecycleService lifecycleService)
    : ChangeEntryStateEndpointBase(lifecycleService)
{
    public override void Configure() => ConfigureMemberMutation(
        "api/vaults/{vaultId:guid}/entries/{entryId:guid}/restore",
        "Restore an encrypted Entry",
        "Restores an Archived or Recently Deleted Entry as a new immutable canonical revision. Deleted grants never reactivate.");

    public override async Task HandleAsync(ChangeEntryStateRequest req, CancellationToken ct) =>
        await SendResultAsync(await LifecycleService.RestoreAsync(
            User.GetOrganizationId()!.Value,
            User.GetUserId()!.Value,
            req,
            ct), ct);
}
