using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class EntryFaker
{
    public static PrivateCtorFaker<VaultEntry> Create(
        Guid? id = null,
        Guid? organizationId = null,
        Guid? vaultId = null,
        Guid? createdBy = null,
        EntryType type = EntryType.Credential)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return (PrivateCtorFaker<VaultEntry>)new PrivateCtorFaker<VaultEntry>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.OrganizationId, organizationId ?? Guid.NewGuid())
            .RuleFor(x => x.VaultId, vaultId ?? Guid.NewGuid())
            .RuleFor(x => x.State, EntryState.Active)
            .RuleFor(x => x.CurrentRevision, new EntryRevision(1))
            .RuleFor(x => x.MemberIndexRevision, new MemberIndexRevision(1))
            .RuleFor(x => x.AgentDiscoveryRevision, (AgentDiscoveryRevision?)null)
            .RuleFor(x => x.CurrentKeyVersion, new EntryKeyVersion(1))
            .RuleFor(x => x.CreatedBy, createdBy ?? Guid.NewGuid())
            .RuleFor(x => x.CreatedAt, now)
            .RuleFor(x => x.UpdatedBy, createdBy ?? Guid.NewGuid())
            .RuleFor(x => x.UpdatedAt, now)
            .RuleFor(x => x.MemberIndexProtocolVersion, VaultProtocol.CurrentVersion)
            .RuleFor(x => x.MemberIndexCryptoSuiteId, CryptoSuiteId.XChaCha20Poly1305V1)
            .RuleFor(x => x.MemberIndexMemberKeyGeneration, new MemberKeyGeneration(1))
            .RuleFor(x => x.MemberIndexEncodedSuitePayload,
                Enumerable.Range(0, 56).Select(i => (byte)i).ToArray())
            .RuleFor(x => x.Keys, (_, entry) =>
            {
                var scope = new EntryScope(entry.OrganizationId, entry.VaultId, entry.Id);
                var header = EntryEnvelopeHeader.Create(
                    2, 1, 2, 8, 1, 1, new MemberKeyGeneration(1),
                    Enumerable.Range(0, 24).Select(i => (byte)i).ToArray(), 8);
                return new List<VaultEntryKey>
                {
                    VaultEntryKey.Create(
                        scope,
                        new EntryKeyWrapperRevision(1),
                        new EntryKeyVersion(1),
                        new MemberKeyGeneration(1),
                        new VaultKeyVersion(1),
                        header,
                        Enumerable.Range(0, 32).Select(i => (byte)(i + 48)).ToArray()),
                };
            })
            .RuleFor(x => x.Versions, (_, entry) => CreateVersions(entry, 1));
    }

    internal static ICollection<VaultEntryVersion> CreateVersions(VaultEntry entry, ulong memberSequence)
    {
        var scope = new EntryScope(entry.OrganizationId, entry.VaultId, entry.Id);
        var header = EntryEnvelopeHeader.Create(
            2, 1, 2, 3, 1, 1, new MemberKeyGeneration(1),
            Enumerable.Range(0, 24).Select(i => (byte)(i + 72)).ToArray(), 3);
        var secret = MemberSecretCiphertext.Create(
            scope,
            new EntryRevision(1),
            EntryOperation.Created,
            header,
            Enumerable.Range(0, 32).Select(i => (byte)(i + 96)).ToArray());
        return new List<VaultEntryVersion>
        {
            VaultEntryVersion.Create(
                secret,
                new AllocatedVaultSequences(new MemberSequence(memberSequence), null),
                memberIndexChanged: true,
                entry.CreatedAt,
                ActorType.Member,
                entry.CreatedBy),
        };
    }
}
