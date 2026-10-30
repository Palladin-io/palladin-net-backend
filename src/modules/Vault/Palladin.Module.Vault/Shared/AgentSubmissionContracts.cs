using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Shared;

// Wire objects frozen by palladin-protocol contracts/agent-submission/v1. Identifiers, u64 values and
// binaries stay strings so the backend can verify their canonical encoding before signature checks.
[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AgentSubmissionTargetContract(
    [property: JsonRequired] string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SuggestedVaultId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VaultId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedEntryRevision = null);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AgentEntrySubmissionContract(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] uint ContractVersion,
    [property: JsonRequired] ushort ProtocolVersion,
    [property: JsonRequired] string OrganizationId,
    [property: JsonRequired] string SubmissionId,
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] ushort SubmissionKind,
    [property: JsonRequired] ushort EntryType,
    [property: JsonRequired] AgentSubmissionTargetContract Target,
    [property: JsonRequired] string SubmissionRevision,
    [property: JsonRequired] string AgentAccessEpoch,
    [property: JsonRequired] uint AgentKeyVersion,
    [property: JsonRequired] string AgentKeyFingerprint,
    [property: JsonRequired] uint ApproverKeyVersion,
    [property: JsonRequired] string ApproverMessageKeyFingerprint,
    [property: JsonRequired] string ApproverManifestRevision,
    [property: JsonRequired] string EncodedPayload,
    [property: JsonRequired] string EncodedAgentWrappedDek,
    [property: JsonRequired] string EncodedApproverWrappedDek,
    [property: JsonRequired] string AgentSignature);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OrganizationApproverManifestContract(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] uint ContractVersion,
    [property: JsonRequired] ushort ProtocolVersion,
    [property: JsonRequired] string OrganizationId,
    [property: JsonRequired] uint KeyVersion,
    [property: JsonRequired] string ManifestRevision,
    [property: JsonRequired] string ApproverSetRevision,
    [property: JsonRequired] string ApproverSetDigest,
    [property: JsonRequired] string MessagePublicKey,
    [property: JsonRequired] string MessageKeyFingerprint,
    [property: JsonRequired] string SigningPublicKey,
    [property: JsonRequired] string SigningKeyFingerprint,
    [property: JsonRequired] string IssuedAt,
    [property: JsonRequired] string Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousSigningKeyFingerprint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContinuitySignature = null);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ApproverKeyWrapDescriptorContract(
    [property: JsonRequired] ushort Purpose,
    [property: JsonRequired] ushort ScopeBitmap,
    [property: JsonRequired] string OrganizationId,
    [property: JsonRequired] string MemberId,
    [property: JsonRequired] string ResourceRevision,
    [property: JsonRequired] uint WrappedKeyVersion,
    [property: JsonRequired] ushort RecipientKeyKind,
    [property: JsonRequired] uint RecipientKeyVersion,
    [property: JsonRequired] string RecipientFingerprint);

[PublicAPI]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproverKeyWrapContract(
    [property: JsonRequired] ApproverKeyWrapDescriptorContract Descriptor,
    [property: JsonRequired] string EncodedSealedKeyPackage);

[PublicAPI]
public sealed record ApproverContract(
    string MemberId,
    uint MemberKeyVersion,
    string MemberKeyFingerprint,
    string MemberPublicKey,
    bool HasWrap);

[PublicAPI]
public sealed record SubmissionErrorResponse(string Code);
