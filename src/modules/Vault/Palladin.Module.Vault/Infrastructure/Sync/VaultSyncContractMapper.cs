using System.Globalization;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal static class VaultSyncContractMapper
{
    internal static MemberSyncItem ToMemberHead(VaultEntry entry, VaultEntryKey entryKey) => new(
        entry.Id,
        VaultSyncProtocol.HeadKind,
        entry.State,
        entry.UpdatedAt,
        entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
        entry.MemberIndexRevision.Value.ToString(CultureInfo.InvariantCulture),
        entry.CurrentKeyVersion.Value,
        VaultEnvelopeContractMapper.ToContract(entryKey),
        VaultEnvelopeContractMapper.ToContract(entry.GetMemberIndex()));

    internal static MemberSyncItem ToMemberTombstone(Guid entryId) => new(
        entryId,
        VaultSyncProtocol.TombstoneKind,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    internal static bool TryToCurrentMemberEntryHead(
        VaultEntry entry,
        VaultEntryKey? entryKey,
        VaultEntryVersion? entryVersion,
        CurrentMemberEntrySyncAuthority authority,
        out CurrentMemberEntrySyncItem item)
    {
        item = null!;
        if (entryKey is null
            || entryVersion is null
            || entry.OrganizationId != authority.OrganizationId
            || entry.VaultId != authority.VaultId
            || entry.MemberIndexRevision.Value != entry.CurrentRevision.Value
            || entry.MemberIndexMemberKeyGeneration != entryVersion.MemberKeyGeneration
            || entry.MemberIndexMemberKeyGeneration.Value > authority.MemberKeyGeneration
            || entryKey.OrganizationId != authority.OrganizationId
            || entryKey.VaultId != authority.VaultId
            || entryKey.EntryId != entry.Id
            || entryKey.KeyVersion != entry.CurrentKeyVersion
            || entryKey.MemberKeyGeneration.Value != authority.MemberKeyGeneration
            || entryKey.WrappingKeyVersion.Value != authority.VaultKeyVersion
            || entryVersion.OrganizationId != authority.OrganizationId
            || entryVersion.VaultId != authority.VaultId
            || entryVersion.EntryId != entry.Id
            || entryVersion.Revision != entry.CurrentRevision
            || entryVersion.KeyVersion != entry.CurrentKeyVersion
            || entryVersion.MemberKeyGeneration.Value > authority.MemberKeyGeneration)
        {
            return false;
        }

        var memberIndex = entry.GetMemberIndex();
        var memberSecret = entryVersion.GetMemberSecret();
        if (memberIndex.Revision.Value != entry.CurrentRevision.Value
            || memberIndex.Header.KeyVersion != entry.CurrentKeyVersion.Value
            || memberIndex.Header.MemberKeyGeneration != entry.MemberIndexMemberKeyGeneration
            || memberSecret.Revision != entry.CurrentRevision
            || memberSecret.Header.KeyVersion != entry.CurrentKeyVersion.Value
            || memberSecret.Header.MemberKeyGeneration != entryVersion.MemberKeyGeneration)
        {
            return false;
        }

        item = new CurrentMemberEntrySyncItem(
            entry.Id,
            VaultSyncProtocol.HeadKind,
            entry.State,
            entry.UpdatedAt,
            entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture),
            entry.MemberIndexRevision.Value.ToString(CultureInfo.InvariantCulture),
            entry.CurrentKeyVersion.Value,
            VaultEnvelopeContractMapper.ToContract(entryKey),
            VaultEnvelopeContractMapper.ToContract(memberIndex),
            VaultEnvelopeContractMapper.ToContract(memberSecret));
        return true;
    }

    internal static CurrentMemberEntrySyncItem ToCurrentMemberEntryTombstone(Guid entryId) => new(
        entryId,
        VaultSyncProtocol.TombstoneKind,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    internal static AgentDiscoverySyncItem ToDiscoveryHead(VaultEntry entry)
    {
        var discovery = entry.GetAgentDiscovery()!;
        return new AgentDiscoverySyncItem(
            entry.Id,
            VaultSyncProtocol.HeadKind,
            discovery.Revision.Value.ToString(CultureInfo.InvariantCulture),
            VaultEnvelopeContractMapper.ToContract(discovery));
    }

    internal static AgentDiscoverySyncItem ToDiscoveryTombstone(Guid entryId) => new(
        entryId,
        VaultSyncProtocol.TombstoneKind,
        null,
        null);
}
