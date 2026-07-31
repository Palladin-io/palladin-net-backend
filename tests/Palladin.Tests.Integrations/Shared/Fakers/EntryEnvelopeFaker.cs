using Microsoft.AspNetCore.WebUtilities;
using Palladin.Core.Types;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class EntryEnvelopeFaker
{
    private const ushort ProtocolVersion = 2;
    private const string CryptoSuiteId = "palladin-vault-xchacha-v1";

    internal static CreateEntryRequest CreateRequest(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        bool includeDiscovery = true,
        int seed = 0) => new()
        {
            VaultId = vaultId,
            EntryId = entryId,
            EntryKey = CreateKey(organizationId, vaultId, entryId, seed: seed),
            MemberIndex = CreateMemberIndex(organizationId, vaultId, entryId, seed: seed),
            MemberSecret = CreateMemberSecret(
            organizationId,
            vaultId,
            entryId,
            revision: 1,
            operation: EntryOperation.Created,
            seed: seed),
            AgentDiscovery = includeDiscovery
            ? CreateAgentDiscovery(organizationId, vaultId, entryId, seed: seed)
            : null,
        };

    internal static ImportEntryItem CreateImportItem(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        bool includeDiscovery = true,
        int seed = 0)
    {
        var request = CreateRequest(organizationId, vaultId, entryId, includeDiscovery, seed);

        return new ImportEntryItem
        {
            EntryId = request.EntryId,
            EntryKey = request.EntryKey,
            MemberIndex = request.MemberIndex,
            MemberSecret = request.MemberSecret,
            AgentDiscovery = request.AgentDiscovery,
        };
    }

    internal static UpdateEntryRequest CreateUpdateRequest(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong baseRevision,
        ulong? memberIndexRevision = null,
        ulong? agentDiscoveryRevision = null,
        uint keyVersion = 1,
        uint memberKeyGeneration = 1,
        uint? newKeyVersion = null,
        bool disableDiscovery = false,
        int seed = 32) => new()
        {
            VaultId = vaultId,
            EntryId = entryId,
            BaseRevision = baseRevision.ToString(),
            NewEntryKey = newKeyVersion is { } nextKeyVersion
            ? CreateKey(
                organizationId,
                vaultId,
                entryId,
                keyVersion: nextKeyVersion,
                memberKeyGeneration: memberKeyGeneration,
                seed: seed)
            : null,
            MemberSecret = CreateMemberSecret(
            organizationId,
            vaultId,
            entryId,
            revision: baseRevision + 1,
            operation: EntryOperation.Updated,
            keyVersion: newKeyVersion ?? keyVersion,
            memberKeyGeneration: memberKeyGeneration,
            seed: seed),
            MemberIndex = memberIndexRevision is { } indexRevision
            ? CreateMemberIndex(
                organizationId,
                vaultId,
                entryId,
                revision: indexRevision,
                keyVersion: newKeyVersion ?? keyVersion,
                memberKeyGeneration: memberKeyGeneration,
                seed: seed)
            : null,
            AgentDiscoveryChanged = agentDiscoveryRevision is not null || disableDiscovery,
            AgentDiscovery = agentDiscoveryRevision is { } discoveryRevision
            ? CreateAgentDiscovery(
                organizationId,
                vaultId,
                entryId,
                revision: discoveryRevision,
                memberKeyGeneration: memberKeyGeneration,
                seed: seed)
            : null,
        };

    internal static ChangeEntryStateRequest CreateStateChangeRequest(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong baseRevision,
        EntryOperation operation,
        ulong? agentDiscoveryRevision = null,
        int seed = 32) => new()
        {
            VaultId = vaultId,
            EntryId = entryId,
            BaseRevision = baseRevision.ToString(),
            MemberSecret = CreateMemberSecret(
            organizationId,
            vaultId,
            entryId,
            baseRevision + 1,
            operation,
            seed: seed),
            AgentDiscovery = agentDiscoveryRevision is { } revision
            ? CreateAgentDiscovery(
                organizationId,
                vaultId,
                entryId,
                revision,
                seed: seed)
            : null,
        };

    internal static VaultEntryKeyContract CreateKey(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong wrapperRevision = 1,
        uint keyVersion = 1,
        uint memberKeyGeneration = 1,
        uint wrappingKeyVersion = 1,
        int seed = 0) => new(
        new EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract>(ProtocolVersion, CryptoSuiteId,
            EnvelopePurposeContract.EntryDekByVaultKey,
            new EnvelopeScopeContract(organizationId, vaultId, EntryId: entryId), wrapperRevision.ToString(),
            keyVersion, memberKeyGeneration, new VaultKeyEnvelopeBindingContract(wrappingKeyVersion)),
        Encode(Payload(seed, seed + 24, 48)));

    internal static MemberIndexEnvelopeContract CreateMemberIndex(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong revision = 1,
        uint keyVersion = 1,
        uint memberKeyGeneration = 1,
        int seed = 0) => new(
        new EnvelopeDescriptorContract<EmptyEnvelopeBindingContract>(ProtocolVersion, CryptoSuiteId,
            EnvelopePurposeContract.MemberIndex,
            new EnvelopeScopeContract(organizationId, vaultId, EntryId: entryId), revision.ToString(),
            keyVersion, memberKeyGeneration, new EmptyEnvelopeBindingContract()),
        Encode(Payload(seed + 1, seed + 72, 48)));

    internal static MemberSecretEnvelopeContract CreateMemberSecret(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong revision,
        EntryOperation operation,
        uint keyVersion = 1,
        uint memberKeyGeneration = 1,
        int seed = 0) => new(
        new EnvelopeDescriptorContract<MemberSecretEnvelopeBindingContract>(ProtocolVersion, CryptoSuiteId,
            EnvelopePurposeContract.MemberSecret,
            new EnvelopeScopeContract(organizationId, vaultId, EntryId: entryId), revision.ToString(),
            keyVersion, memberKeyGeneration, new MemberSecretEnvelopeBindingContract(operation)),
        Encode(Payload(seed + 2, seed + 120, 64)));

    internal static AgentDiscoveryEnvelopeContract CreateAgentDiscovery(
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        ulong revision = 1,
        uint vdkVersion = 1,
        uint memberKeyGeneration = 1,
        int seed = 0) => new(
        new EnvelopeDescriptorContract<EmptyEnvelopeBindingContract>(ProtocolVersion, CryptoSuiteId,
            EnvelopePurposeContract.AgentDiscovery,
            new EnvelopeScopeContract(organizationId, vaultId, EntryId: entryId), revision.ToString(),
            vdkVersion, memberKeyGeneration, new EmptyEnvelopeBindingContract()),
        Encode(Payload(seed + 3, seed + 184, 48)));

    private static byte[] Payload(int nonceSeed, int ciphertextSeed, int ciphertextBytes) =>
        Bytes(nonceSeed, 24).Concat(Bytes(ciphertextSeed, ciphertextBytes)).ToArray();

    private static byte[] Bytes(int seed, int count) =>
        Enumerable.Range(0, count).Select(index => (byte)(seed + index)).ToArray();

    private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);
}
