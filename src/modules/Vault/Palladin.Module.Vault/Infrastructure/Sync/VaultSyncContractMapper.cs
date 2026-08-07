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
