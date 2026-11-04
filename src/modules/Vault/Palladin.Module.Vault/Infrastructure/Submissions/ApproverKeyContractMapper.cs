using System.Text.Json;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Submissions;

internal static class ApproverKeyContractMapper
{
    internal const ushort MemberRecipientKeyKind = 5;

    internal static OrganizationApproverManifestContract ToManifestContract(string document) =>
        JsonSerializer.Deserialize<OrganizationApproverManifestContract>(document, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Stored approver manifest is empty.");

    internal static AgentEntrySubmissionContract ToSubmissionContract(string document) =>
        JsonSerializer.Deserialize<AgentEntrySubmissionContract>(document, JsonSerializerOptions.Web)
        ?? throw new InvalidOperationException("Stored Agent submission is empty.");

    internal static ApproverKeyWrapContract ToContract(ApproverKeyWrap wrap) => new(
        new ApproverKeyWrapDescriptorContract(
            wrap.Purpose,
            AgentSubmissionCrypto.ApproverWrapScopeBitmap,
            AgentSubmissionCrypto.FormatUuid(wrap.OrganizationId),
            AgentSubmissionCrypto.FormatUuid(wrap.MemberId),
            wrap.ApproverSetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            wrap.KeyVersion,
            MemberRecipientKeyKind,
            wrap.RecipientKeyVersion.Value,
            AgentSubmissionCrypto.Encode(wrap.RecipientFingerprint),
            wrap.WrapperSuiteId),
        AgentSubmissionCrypto.Encode(wrap.SealedPackage));

    internal static ApproverContract ToContract(CurrentApprover approver, bool hasWrap) => new(
        AgentSubmissionCrypto.FormatUuid(approver.MemberId),
        approver.KeyVersion,
        AgentSubmissionCrypto.Encode(approver.Fingerprint),
        AgentSubmissionCrypto.Encode(approver.PublicKey),
        hasWrap,
        approver.KeySuiteId);

    // Every recipient must receive exactly one message-key wrap and one signing-seed wrap, each bound to the
    // authenticated organization, the recipient's current Member key and the committed key/set revision.
    internal static List<ApproverKeyWrap> ToDomain(
        IReadOnlyList<ApproverKeyWrapContract>? wraps,
        Guid organizationId,
        uint keyVersion,
        ulong approverSetRevision,
        IReadOnlyCollection<CurrentApprover> recipients)
    {
        if (wraps is null || recipients.Count == 0 || wraps.Count != recipients.Count * 2)
        {
            throw new AgentSubmissionInputException("invalid-wraps");
        }

        var byMember = recipients.ToDictionary(x => x.MemberId);
        var result = new List<ApproverKeyWrap>(wraps.Count);
        foreach (var wrap in wraps)
        {
            var descriptor = wrap?.Descriptor ?? throw new AgentSubmissionInputException("invalid-wraps");
            var memberId = AgentSubmissionCrypto.ParseUuid(descriptor.MemberId, "invalid-wraps");
            if (AgentSubmissionCrypto.ParseUuid(descriptor.OrganizationId, "invalid-wraps") != organizationId
                || descriptor.ScopeBitmap != AgentSubmissionCrypto.ApproverWrapScopeBitmap
                || descriptor.Purpose is not (ApproverKeyWrap.MessagePrivateKeyPurpose or ApproverKeyWrap.SigningPrivateKeyPurpose)
                || AgentSubmissionCrypto.ParseUInt64(descriptor.ResourceRevision, code: "invalid-wraps") != approverSetRevision
                || descriptor.WrappedKeyVersion != keyVersion
                || descriptor.RecipientKeyKind != MemberRecipientKeyKind
                || !byMember.TryGetValue(memberId, out var recipient)
                || descriptor.RecipientKeyVersion != recipient.KeyVersion
                || (descriptor.WrapperSuiteId is not null && descriptor.WrapperSuiteId != recipient.KeySuiteId)
                || !Palladin.Core.Cryptography.KeyWrapperSuites.ResolveForOpen(recipient.KeySuiteId).CanSeal
                || !AgentSubmissionCrypto.DecodeBinary(descriptor.RecipientFingerprint, 32, "invalid-wraps")
                    .AsSpan().SequenceEqual(recipient.Fingerprint))
            {
                throw new AgentSubmissionInputException("invalid-wraps");
            }

            result.Add(ApproverKeyWrap.Create(
                organizationId,
                memberId,
                descriptor.Purpose,
                keyVersion,
                approverSetRevision,
                new MemberRecipientKeyVersion(recipient.KeyVersion),
                recipient.Fingerprint,
                recipient.KeySuiteId,
                AgentSubmissionCrypto.DecodeBinary(
                    wrap.EncodedSealedKeyPackage,
                    Palladin.Core.Cryptography.KeyWrapperSuites.ResolveForOpen(recipient.KeySuiteId).PackageBytes,
                    "invalid-wraps")));
        }

        if (result.Select(x => (x.MemberId, x.Purpose)).Distinct().Count() != result.Count)
        {
            throw new AgentSubmissionInputException("invalid-wraps");
        }

        return result;
    }
}
