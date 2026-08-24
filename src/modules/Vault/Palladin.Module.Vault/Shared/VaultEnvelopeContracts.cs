using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Palladin.Core.Types;

namespace Palladin.Module.Vault.Shared;

[PublicAPI]
public enum EnvelopePurposeContract : ushort
{
    MemberVaultMetadata = 1,
    VaultDiscoveryKey = 2,
    VaultAgentMessagePrivateKey = 3,
    VaultManifestSigningPrivateKey = 4,
    MemberIndex = 5,
    MemberSecret = 6,
    AgentDiscovery = 7,
    EntryDekByVaultKey = 8,
    EncryptedReason = 9,
    GrantPayload = 10,
}

[PublicAPI]
public sealed record EnvelopeScopeContract(
    Guid OrganizationId,
    Guid VaultId,
    Guid? EntryId = null,
    Guid? GrantOrRequestId = null,
    Guid? AgentId = null,
    Guid? MemberId = null);

[PublicAPI]
public sealed record EnvelopeDescriptorContract<TBinding>(
    ushort ProtocolVersion,
    string CryptoSuiteId,
    EnvelopePurposeContract Purpose,
    EnvelopeScopeContract Scope,
    string ResourceRevision,
    uint KeyVersion,
    uint? MemberKeyGeneration,
    TBinding Binding);

[PublicAPI]
public sealed record EmptyEnvelopeBindingContract;

[PublicAPI]
public sealed record MemberSecretEnvelopeBindingContract(EntryOperation Operation);

[PublicAPI]
public sealed record VaultKeyEnvelopeBindingContract(uint WrappingVaultKeyVersion);

[PublicAPI]
public enum X25519WrapperPurposeContract : ushort
{
    MemberVaultKey = 1,
    AgentDiscoveryVdk = 2,
    ReasonDek = 3,
    GrantDek = 4,
    AgentVaultKey = 5,
}

[PublicAPI]
public enum X25519RecipientKeyKindContract : ushort
{
    AgentX25519 = 1,
    VaultMessageX25519 = 4,
    MemberX25519 = 5,
}

[PublicAPI]
public sealed record X25519WrapperDescriptorContract(
    ushort ProtocolVersion,
    string WrapperSuiteId,
    X25519WrapperPurposeContract Purpose,
    EnvelopeScopeContract Scope,
    string ResourceRevision,
    uint WrappedKeyVersion,
    uint? MemberKeyGeneration,
    X25519RecipientKeyKindContract RecipientKeyKind,
    uint RecipientKeyVersion,
    string RecipientFingerprint,
    string? ParentDescriptorHash);

[PublicAPI]
public sealed record X25519WrappedKeyContract(
    X25519WrapperDescriptorContract Descriptor,
    string EncodedSealedKeyPackage);

[PublicAPI]
public sealed record AgentWrappedVaultKeyContract(
    X25519WrappedKeyContract WrappedVaultKey)
{
    [JsonIgnore] public Guid OrganizationId => WrappedVaultKey.Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => WrappedVaultKey.Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid GrantId => WrappedVaultKey.Descriptor.Scope.GrantOrRequestId!.Value;
    [JsonIgnore] public Guid AgentId => WrappedVaultKey.Descriptor.Scope.AgentId!.Value;
    [JsonIgnore] public string AgentAccessEpoch => WrappedVaultKey.Descriptor.ResourceRevision;
    [JsonIgnore] public uint VaultKeyVersion => WrappedVaultKey.Descriptor.WrappedKeyVersion;
    [JsonIgnore] public uint RecipientAgentKeyVersion => WrappedVaultKey.Descriptor.RecipientKeyVersion;
    [JsonIgnore] public string RecipientAgentKeyFingerprint => WrappedVaultKey.Descriptor.RecipientFingerprint;
    [JsonIgnore] public string EncodedSealedVaultKeyPackage => WrappedVaultKey.EncodedSealedKeyPackage;
}

[PublicAPI]
public sealed record MemberVaultMetadataEnvelopeContract(
    EnvelopeDescriptorContract<EmptyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public string MetadataRevision => Descriptor.ResourceRevision;
}

[PublicAPI]
public sealed record MemberVaultKeyEnvelopeContract(
    X25519WrappedKeyContract WrappedVaultKey)
{
    [JsonIgnore] public Guid OrganizationId => WrappedVaultKey.Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => WrappedVaultKey.Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid MemberId => WrappedVaultKey.Descriptor.Scope.MemberId!.Value;
    [JsonIgnore] public uint VkVersion => WrappedVaultKey.Descriptor.WrappedKeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => WrappedVaultKey.Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint RecipientMemberKeyVersion => WrappedVaultKey.Descriptor.RecipientKeyVersion;
    [JsonIgnore] public string RecipientMemberKeyFingerprint => WrappedVaultKey.Descriptor.RecipientFingerprint;
    [JsonIgnore] public string SealedVaultKeyPackage => WrappedVaultKey.EncodedSealedKeyPackage;
    [JsonIgnore] public string WrapperSuiteId => WrappedVaultKey.Descriptor.WrapperSuiteId;
}

[PublicAPI]
public sealed record VaultKeyEpochContract(
    uint VaultKeyVersion,
    uint VdkVersion,
    uint AgentMessageKeyVersion,
    uint ManifestSigningKeyVersion);

[PublicAPI]
public enum VaultPublicKeyKindContract : ushort
{
    AgentMessageX25519 = 1,
    ManifestSigningEd25519 = 2,
}

[PublicAPI]
public sealed record VaultPublicKeyContract(
    ushort ProtocolVersion,
    string SchemeId,
    VaultPublicKeyKindContract KeyKind,
    uint KeyVersion,
    string EncodedPublicKey,
    string Fingerprint);

[PublicAPI]
public sealed record VaultDiscoveryKeyEnvelopeContract(
    EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public string DiscoveryKeyRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public uint VdkVersion => Descriptor.KeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint WrappingKeyVersion => Descriptor.Binding.WrappingVaultKeyVersion;
}

[PublicAPI]
public sealed record VaultPrivateKeyEnvelopeContract(
    EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public ushort PrivateKeyKind => Descriptor.Purpose == EnvelopePurposeContract.VaultAgentMessagePrivateKey ? (ushort)1 : (ushort)2;
    [JsonIgnore] public string PrivateKeyRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public uint PrivateKeyVersion => Descriptor.KeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint WrappingKeyVersion => Descriptor.Binding.WrappingVaultKeyVersion;
}

[PublicAPI]
public sealed record MemberIndexEnvelopeContract(
    EnvelopeDescriptorContract<EmptyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public string MemberIndexRevision => Descriptor.ResourceRevision;
}

[PublicAPI]
public sealed record MemberSecretEnvelopeContract(
    EnvelopeDescriptorContract<MemberSecretEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public string Revision => Descriptor.ResourceRevision;
    [JsonIgnore] public EntryOperation Operation => Descriptor.Binding.Operation;
}

[PublicAPI]
public sealed record AgentDiscoveryEnvelopeContract(
    EnvelopeDescriptorContract<EmptyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public string AgentDiscoveryRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public uint VdkVersion => Descriptor.KeyVersion;
}

[PublicAPI]
public sealed record VaultEntryKeyContract(
    EnvelopeDescriptorContract<VaultKeyEnvelopeBindingContract> Descriptor,
    string EncodedSuitePayload)
{
    [JsonIgnore] public Guid OrganizationId => Descriptor.Scope.OrganizationId;
    [JsonIgnore] public Guid VaultId => Descriptor.Scope.VaultId;
    [JsonIgnore] public Guid EntryId => Descriptor.Scope.EntryId!.Value;
    [JsonIgnore] public string WrapperRevision => Descriptor.ResourceRevision;
    [JsonIgnore] public uint KeyVersion => Descriptor.KeyVersion;
    [JsonIgnore] public uint MemberKeyGeneration => Descriptor.MemberKeyGeneration!.Value;
    [JsonIgnore] public uint WrappingKeyVersion => Descriptor.Binding.WrappingVaultKeyVersion;
}
