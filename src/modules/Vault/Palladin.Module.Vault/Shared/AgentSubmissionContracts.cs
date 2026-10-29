using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Shared;

// Wire objects frozen by palladin-protocol contracts/agent-submission/v1. Identifiers, u64 values and
// binaries stay strings so the backend can verify their canonical encoding before signature checks.
[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AgentSubmissionTargetContract(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedVaultId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VaultId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedEntryRevision = null);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AgentEntrySubmissionContract(
    string Schema,
    uint ContractVersion,
    ushort ProtocolVersion,
    string OrganizationId,
    string SubmissionId,
    string AgentId,
    ushort SubmissionKind,
    ushort EntryType,
    AgentSubmissionTargetContract Target,
    string SubmissionRevision,
    string AgentAccessEpoch,
    uint AgentKeyVersion,
    string AgentKeyFingerprint,
    uint ApproverKeyVersion,
    string ApproverMessageKeyFingerprint,
    string ApproverManifestRevision,
    string EncodedPayload,
    string EncodedAgentWrappedDek,
    string EncodedApproverWrappedDek,
    string AgentSignature);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OrganizationApproverManifestContract(
    string Schema,
    uint ContractVersion,
    ushort ProtocolVersion,
    string OrganizationId,
    uint KeyVersion,
    string ManifestRevision,
    string ApproverSetRevision,
    string ApproverSetDigest,
    string MessagePublicKey,
    string MessageKeyFingerprint,
    string SigningPublicKey,
    string SigningKeyFingerprint,
    string IssuedAt,
    string Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousSigningKeyFingerprint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContinuitySignature = null);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ApproverKeyWrapDescriptorContract(
    ushort Purpose,
    ushort ScopeBitmap,
    string OrganizationId,
    string MemberId,
    string ResourceRevision,
    uint WrappedKeyVersion,
    ushort RecipientKeyKind,
    uint RecipientKeyVersion,
    string RecipientFingerprint);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproverKeyWrapContract(
    ApproverKeyWrapDescriptorContract Descriptor,
    string EncodedSealedKeyPackage);

[PublicAPI]
public sealed record ApproverContract(
    string MemberId,
    uint MemberKeyVersion,
    string MemberKeyFingerprint,
    string MemberPublicKey,
    bool HasWrap);

[PublicAPI]
public sealed record SubmissionErrorResponse(string Code);
