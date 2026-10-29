using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NodaTime;
using NodaTime.Text;
using NSec.Cryptography;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal sealed class AgentSubmissionInputException(string code) : Exception($"Agent submission input rejected: {code}.")
{
    internal string Code { get; } = code;
}

internal sealed record ParsedAgentSubmission(
    Guid OrganizationId,
    Guid SubmissionId,
    Guid AgentId,
    AgentSubmissionKind Kind,
    AgentSubmissionEntryType EntryType,
    AgentSubmissionTarget Target,
    ulong SubmissionRevision,
    ulong AgentAccessEpoch,
    uint AgentKeyVersion,
    byte[] AgentKeyFingerprint,
    uint ApproverKeyVersion,
    byte[] ApproverMessageKeyFingerprint,
    ulong ApproverManifestRevision,
    string EncodedPayload,
    string IdentityDocument,
    string Document,
    byte[] SignatureInput,
    byte[] Signature);

// Structural and signature verification of the Agent Submission contract v1. The backend never holds
// a private key, so it verifies everything else: closed shapes, canonical encodings, limits, the
// Agent signature, approver manifest self/continuity signatures, fingerprints and the approver set digest.
internal static class AgentSubmissionCrypto
{
    internal const string SubmissionSchema = "palladin.agent-entry-submission.v1";
    internal const string ManifestSchema = "palladin.organization-approver-manifest.v1";
    internal const ushort ApproverWrapScopeBitmap = 33;

    private static readonly byte[] SubmissionSignatureDomain = "PLDNSUB1SIG:AGENT-SUBMISSION:"u8.ToArray();
    private static readonly byte[] ManifestSignatureDomain = "PLDNSUB1SIG:APPROVER-MANIFEST:"u8.ToArray();
    private static readonly byte[] ContinuitySignatureDomain = "PLDNSUB1SIG:APPROVER-CONTINUITY:"u8.ToArray();
    private static readonly byte[] ApproverSetDomain = "PLDNSUB1SET"u8.ToArray();
    private static readonly InstantPattern IssuedAtPattern = InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss'Z'");

    internal static ParsedAgentSubmission ParseSubmission(AgentEntrySubmissionContract? contract)
    {
        if (contract is null || contract.Target is null
            || contract.Schema != SubmissionSchema
            || contract.ContractVersion != AgentSubmissionLimits.ContractVersion
            || contract.ProtocolVersion != VaultProtocol.CurrentVersion)
        {
            throw new AgentSubmissionInputException("invalid-input");
        }

        if (contract.EntryType is not ((ushort)AgentSubmissionEntryType.Key or (ushort)AgentSubmissionEntryType.Credential))
        {
            throw new AgentSubmissionInputException("unsupported-entry-type");
        }

        var kind = contract.SubmissionKind switch
        {
            (ushort)AgentSubmissionKind.New => AgentSubmissionKind.New,
            (ushort)AgentSubmissionKind.Update => AgentSubmissionKind.Update,
            _ => throw new AgentSubmissionInputException("invalid-input"),
        };
        var (target, targetMap) = ParseTarget(contract.Target, kind);

        var payload = DecodeBinary(contract.EncodedPayload);
        if (payload.Length < AgentSubmissionLimits.PayloadNonceBytes + AgentSubmissionLimits.PayloadTagBytes
            || payload.Length > AgentSubmissionLimits.MaximumPayloadBytes)
        {
            throw new AgentSubmissionInputException("invalid-input");
        }

        DecodeBinary(contract.EncodedAgentWrappedDek, X25519SealedBoxContract.EncodedPackageBytes);
        DecodeBinary(contract.EncodedApproverWrappedDek, X25519SealedBoxContract.EncodedPackageBytes);
        var signature = DecodeBinary(contract.AgentSignature, 64);
        var agentKeyFingerprint = DecodeBinary(contract.AgentKeyFingerprint, VaultProtocol.FingerprintBytes);
        var approverMessageKeyFingerprint = DecodeBinary(
            contract.ApproverMessageKeyFingerprint, VaultProtocol.FingerprintBytes);
        if (contract.AgentKeyVersion == 0 || contract.ApproverKeyVersion == 0)
        {
            throw new AgentSubmissionInputException("invalid-input");
        }

        var identity = new Dictionary<string, object>
        {
            ["contractVersion"] = contract.ContractVersion,
            ["protocolVersion"] = contract.ProtocolVersion,
            ["organizationId"] = CanonicalUuidString(contract.OrganizationId),
            ["submissionId"] = CanonicalUuidString(contract.SubmissionId),
            ["agentId"] = CanonicalUuidString(contract.AgentId),
            ["submissionKind"] = contract.SubmissionKind,
            ["entryType"] = contract.EntryType,
            ["target"] = targetMap,
        };
        var unsigned = new Dictionary<string, object>(identity)
        {
            ["schema"] = contract.Schema,
            ["submissionRevision"] = CanonicalUInt64String(contract.SubmissionRevision),
            ["agentAccessEpoch"] = CanonicalUInt64String(contract.AgentAccessEpoch, allowZero: true),
            ["agentKeyVersion"] = contract.AgentKeyVersion,
            ["agentKeyFingerprint"] = contract.AgentKeyFingerprint,
            ["approverKeyVersion"] = contract.ApproverKeyVersion,
            ["approverMessageKeyFingerprint"] = contract.ApproverMessageKeyFingerprint,
            ["approverManifestRevision"] = CanonicalUInt64String(contract.ApproverManifestRevision),
            ["encodedPayload"] = contract.EncodedPayload,
            ["encodedAgentWrappedDek"] = contract.EncodedAgentWrappedDek,
            ["encodedApproverWrappedDek"] = contract.EncodedApproverWrappedDek,
        };
        var signed = new Dictionary<string, object>(unsigned) { ["agentSignature"] = contract.AgentSignature };

        return new ParsedAgentSubmission(
            ParseUuid(contract.OrganizationId),
            ParseUuid(contract.SubmissionId),
            ParseUuid(contract.AgentId),
            kind,
            (AgentSubmissionEntryType)contract.EntryType,
            target,
            ParseUInt64(contract.SubmissionRevision),
            ParseUInt64(contract.AgentAccessEpoch, allowZero: true),
            contract.AgentKeyVersion,
            agentKeyFingerprint,
            contract.ApproverKeyVersion,
            approverMessageKeyFingerprint,
            ParseUInt64(contract.ApproverManifestRevision),
            contract.EncodedPayload,
            CanonicalJson.SerializeToString(identity),
            CanonicalJson.SerializeToString(signed),
            SignatureInput(SubmissionSignatureDomain, unsigned),
            signature);
    }

    internal static bool VerifyAgentSignature(ParsedAgentSubmission submission, byte[] agentEd25519PublicKey) =>
        Verify(agentEd25519PublicKey, submission.SignatureInput, submission.Signature);

    internal static ApproverManifest ParseManifest(OrganizationApproverManifestContract? contract)
    {
        if (contract is null || contract.Schema != ManifestSchema
            || contract.ContractVersion != AgentSubmissionLimits.ContractVersion
            || contract.ProtocolVersion != VaultProtocol.CurrentVersion
            || contract.KeyVersion == 0
            || (contract.ContinuitySignature is null) != (contract.PreviousSigningKeyFingerprint is null)
            || (contract.KeyVersion == 1 && contract.ContinuitySignature is not null)
            || contract.IssuedAt is null || !IssuedAtPattern.Parse(contract.IssuedAt).Success
            || IssuedAtPattern.Format(IssuedAtPattern.Parse(contract.IssuedAt).Value) != contract.IssuedAt)
        {
            throw new AgentSubmissionInputException("invalid-manifest");
        }

        var messagePublicKey = DecodeBinary(contract.MessagePublicKey, 32, "invalid-manifest");
        var signingPublicKey = DecodeBinary(contract.SigningPublicKey, 32, "invalid-manifest");
        var messageFingerprint = DecodeBinary(contract.MessageKeyFingerprint, 32, "invalid-manifest");
        var signingFingerprint = DecodeBinary(contract.SigningKeyFingerprint, 32, "invalid-manifest");
        var approverSetDigest = DecodeBinary(contract.ApproverSetDigest, 32, "invalid-manifest");
        var signature = DecodeBinary(contract.Signature, 64, "invalid-manifest");
        var previousSigningKeyFingerprint = contract.PreviousSigningKeyFingerprint is null
            ? null
            : DecodeBinary(contract.PreviousSigningKeyFingerprint, 32, "invalid-manifest");
        if (contract.ContinuitySignature is not null)
        {
            DecodeBinary(contract.ContinuitySignature, 64, "invalid-manifest");
        }

        if (!CryptographicOperations.FixedTimeEquals(messageFingerprint,
                VaultKeyFingerprint.Compute(messagePublicKey, VaultKeyKind.OrganizationApproverX25519))
            || !CryptographicOperations.FixedTimeEquals(signingFingerprint,
                VaultKeyFingerprint.Compute(signingPublicKey, VaultKeyKind.OrganizationApproverEd25519)))
        {
            throw new AgentSubmissionInputException("manifest-fingerprint-mismatch");
        }

        var unsigned = ManifestUnsigned(contract);
        if (!Verify(signingPublicKey, SignatureInput(ManifestSignatureDomain, unsigned), signature))
        {
            throw new AgentSubmissionInputException("manifest-signature-invalid");
        }

        var signed = new Dictionary<string, object>(unsigned) { ["signature"] = contract.Signature };
        if (contract.ContinuitySignature is not null)
        {
            signed["continuitySignature"] = contract.ContinuitySignature;
        }

        return new ApproverManifest(
            ParseUuid(contract.OrganizationId, "invalid-manifest"),
            contract.KeyVersion,
            ParseUInt64(contract.ManifestRevision, code: "invalid-manifest"),
            ParseUInt64(contract.ApproverSetRevision, code: "invalid-manifest"),
            approverSetDigest,
            messagePublicKey,
            messageFingerprint,
            signingPublicKey,
            signingFingerprint,
            previousSigningKeyFingerprint,
            CanonicalJson.SerializeToString(signed));
    }

    internal static bool VerifyContinuity(OrganizationApproverManifestContract contract, byte[] previousSigningPublicKey) =>
        contract.ContinuitySignature is not null
        && Verify(previousSigningPublicKey, SignatureInput(ContinuitySignatureDomain, ManifestUnsigned(contract)),
            DecodeBinary(contract.ContinuitySignature, 64, "invalid-manifest"));

    internal static byte[] ComputeApproverSetDigest(IEnumerable<(Guid MemberId, uint KeyVersion, byte[] Fingerprint)> approvers)
    {
        using var stream = new MemoryStream();
        stream.Write(ApproverSetDomain);
        Span<byte> buffer = stackalloc byte[16];
        foreach (var approver in approvers.OrderBy(x => UuidBytes(x.MemberId), ByteOrder.Instance))
        {
            approver.MemberId.TryWriteBytes(buffer, bigEndian: true, out _);
            stream.Write(buffer);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, approver.KeyVersion);
            stream.Write(buffer[..4]);
            stream.Write(approver.Fingerprint);
        }

        return SHA256.HashData(stream.ToArray());
    }

    internal static string FormatIssuedAt(Instant value) => IssuedAtPattern.Format(value);

    internal static Instant TruncateToSeconds(Instant value) =>
        Instant.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    internal static string Encode(byte[] value) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(value);

    internal static string FormatUuid(Guid value) => value.ToString("D");

    internal static Guid ParseUuid(string? value, string code = "invalid-input")
    {
        if (value is null || !Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty
            || parsed.ToString("D") != value)
        {
            throw new AgentSubmissionInputException(code);
        }

        return parsed;
    }

    internal static ulong ParseUInt64(string? value, bool allowZero = false, string code = "invalid-input")
    {
        if (value is null || value.Length is 0 or > 20 || (value.Length > 1 && value[0] == '0')
            || !value.All(char.IsAsciiDigit)
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || (!allowZero && parsed == 0))
        {
            throw new AgentSubmissionInputException(code);
        }

        return parsed;
    }

    internal static byte[] DecodeBinary(string? value, int? exactLength = null, string code = "invalid-input")
    {
        byte[] decoded;
        try
        {
            decoded = VaultEnvelopeContractMapper.DecodeCanonicalBase64Url(value ?? string.Empty);
        }
        catch (DomainException)
        {
            throw new AgentSubmissionInputException(code);
        }

        if (exactLength is { } length && decoded.Length != length)
        {
            throw new AgentSubmissionInputException(code);
        }

        return decoded;
    }

    private static (AgentSubmissionTarget Target, Dictionary<string, object> Canonical) ParseTarget(
        AgentSubmissionTargetContract target,
        AgentSubmissionKind kind)
    {
        if (kind == AgentSubmissionKind.New)
        {
            if (target.Kind != "new" || target.VaultId is not null || target.EntryId is not null
                || target.ExpectedEntryRevision is not null)
            {
                throw new AgentSubmissionInputException("invalid-input");
            }

            var canonical = new Dictionary<string, object> { ["kind"] = "new" };
            Guid? suggestedVaultId = null;
            if (target.SuggestedVaultId is not null)
            {
                suggestedVaultId = ParseUuid(target.SuggestedVaultId);
                canonical["suggestedVaultId"] = target.SuggestedVaultId;
            }

            return (new AgentSubmissionTarget(suggestedVaultId, null, null, null), canonical);
        }

        if (target.Kind != "update" || target.SuggestedVaultId is not null)
        {
            throw new AgentSubmissionInputException("invalid-input");
        }

        return (
            new AgentSubmissionTarget(null, ParseUuid(target.VaultId), ParseUuid(target.EntryId),
                ParseUInt64(target.ExpectedEntryRevision)),
            new Dictionary<string, object>
            {
                ["kind"] = "update",
                ["vaultId"] = target.VaultId!,
                ["entryId"] = target.EntryId!,
                ["expectedEntryRevision"] = target.ExpectedEntryRevision!,
            });
    }

    private static Dictionary<string, object> ManifestUnsigned(OrganizationApproverManifestContract contract)
    {
        var unsigned = new Dictionary<string, object>
        {
            ["schema"] = contract.Schema,
            ["contractVersion"] = contract.ContractVersion,
            ["protocolVersion"] = contract.ProtocolVersion,
            ["organizationId"] = CanonicalUuidString(contract.OrganizationId, "invalid-manifest"),
            ["keyVersion"] = contract.KeyVersion,
            ["manifestRevision"] = CanonicalUInt64String(contract.ManifestRevision, code: "invalid-manifest"),
            ["approverSetRevision"] = CanonicalUInt64String(contract.ApproverSetRevision, code: "invalid-manifest"),
            ["approverSetDigest"] = contract.ApproverSetDigest,
            ["messagePublicKey"] = contract.MessagePublicKey,
            ["messageKeyFingerprint"] = contract.MessageKeyFingerprint,
            ["signingPublicKey"] = contract.SigningPublicKey,
            ["signingKeyFingerprint"] = contract.SigningKeyFingerprint,
            ["issuedAt"] = contract.IssuedAt,
        };
        if (contract.PreviousSigningKeyFingerprint is not null)
        {
            unsigned["previousSigningKeyFingerprint"] = contract.PreviousSigningKeyFingerprint;
        }

        return unsigned;
    }

    private static string CanonicalUuidString(string? value, string code = "invalid-input")
    {
        ParseUuid(value, code);
        return value!;
    }

    private static string CanonicalUInt64String(string? value, bool allowZero = false, string code = "invalid-input")
    {
        ParseUInt64(value, allowZero, code);
        return value!;
    }

    private static byte[] SignatureInput(byte[] domain, IReadOnlyDictionary<string, object> unsigned)
    {
        var canonical = CanonicalJson.Serialize(unsigned);
        var input = new byte[domain.Length + sizeof(ushort) + canonical.Length];
        domain.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(domain.Length), VaultProtocol.CurrentVersion);
        canonical.CopyTo(input, domain.Length + sizeof(ushort));
        return input;
    }

    private static bool Verify(byte[] publicKey, byte[] input, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            var key = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(key, input, signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] UuidBytes(Guid value)
    {
        var bytes = new byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
