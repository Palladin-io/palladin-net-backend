using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal sealed class EntryShareAuthority(VaultDomainWriteContext context)
{
    internal async Task<EntryShareSource> LoadSenderSourceAsync(
        EntryScope scope, Guid senderId, uint authorizationVersion, CancellationToken ct)
    {
        var source = await LoadSourceAsync(scope, senderId, initializeOrganization: true, ct);
        var authority = await context.EntryShareSenderAuthorities.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.UserId == senderId, ct);
        if (authority is null)
        {
            authority = EntryShareSenderAuthority.Create(scope.OrganizationId, senderId);
            context.Add(authority);
        }

        if (!authority.Allows(authorizationVersion))
        {
            throw new EntryShareUnavailableException();
        }

        context.MarkPropertyAsUpdated(authority, x => x.MutationVersion);
        return source;
    }

    internal async Task EnsureRecipientSourceAsync(EntryShare share, CancellationToken ct)
    {
        var source = await LoadSourceAsync(new EntryScope(share.OrganizationId, share.VaultId, share.EntryId),
            share.CreatedBy, initializeOrganization: false, ct);
        var authority = await context.EntryShareSenderAuthorities.SingleOrDefaultAsync(
            x => x.OrganizationId == share.OrganizationId && x.UserId == share.CreatedBy, ct);
        if (authority is null || !authority.Allows(share.SenderAuthorizationVersion)
            || source.Member.AddedAt != share.SenderVaultMembershipAddedAt
            || source.Entry.SharingRevokedThroughRevision >= share.SourceRevision.Value)
        {
            throw new EntryShareUnavailableException();
        }

        context.MarkPropertyAsUpdated(authority, x => x.MutationVersion);
    }

    private async Task<EntryShareSource> LoadSourceAsync(
        EntryScope scope, Guid senderId, bool initializeOrganization, CancellationToken ct)
    {
        var vault = await context.Vaults.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.Id == scope.VaultId, ct);
        var member = await context.VaultMembers.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.VaultId == scope.VaultId && x.UserId == senderId, ct);
        var entry = await context.Entries.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.VaultId == scope.VaultId && x.Id == scope.EntryId, ct);
        if (vault is null || vault.IsDeleting || member is null || entry is null || entry.State != EntryState.Active)
        {
            throw new EntryShareUnavailableException();
        }

        var organization = await context.VaultOrganizationLifecycles.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId, ct);
        if (organization is null)
        {
            if (!initializeOrganization)
            {
                throw new EntryShareUnavailableException();
            }

            organization = VaultOrganizationLifecycle.Create(scope.OrganizationId);
            context.Add(organization);
        }

        if (organization.SharingDisabled)
        {
            throw new EntryShareUnavailableException();
        }

        context.MarkPropertyAsUpdated(organization, x => x.MutationVersion);
        context.MarkPropertyAsUpdated(vault, x => x.MutationVersion);
        context.MarkPropertyAsUpdated(entry, x => x.UpdatedAt);
        return new EntryShareSource(vault, entry, member);
    }
}

internal sealed record EntryShareSource(Domain.Vault Vault, VaultEntry Entry, VaultMember Member);
