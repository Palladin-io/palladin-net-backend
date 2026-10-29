using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using NSec.Cryptography;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Backend-relevant negative cases of palladin-protocol contracts/agent-submission/test/generate-fixture.mjs
// (runNegativeCases, contract 597535b). Candidates are re-signed with the fixture's deterministic keys, so a
// rejection proves the closed-shape/encoding rule rather than a broken signature. Cases that require opening a
// sealed DEK or decrypting the payload (wrapper-context, payload content) are client-only by design.
public sealed class AgentSubmissionContractNegativeTests
{
    private static AgentSubmissionFixture Fixture => AgentSubmissionFixture.Current;

    public static TheoryData<string, string> SubmissionShapeCases => new()
    {
        { "unknown transport schema", """{"schema":"palladin.agent-entry-submission.v2"}""" },
        { "unknown contract version", """{"contractVersion":2}""" },
        { "unknown protocol version", """{"protocolVersion":3}""" },
        { "noncanonical submission revision", """{"submissionRevision":"01"}""" },
        { "zero submission revision", """{"submissionRevision":"0"}""" },
        { "zero access epoch", """{"agentAccessEpoch":"0"}""" },
        { "signed access epoch", """{"agentAccessEpoch":"+4"}""" },
        { "zero manifest revision", """{"approverManifestRevision":"0"}""" },
        { "noncanonical expected revision", """{"target":{"kind":"update","vaultId":"0e0e0e0e-0e0e-4e0e-8e0e-0e0e0e0e0e0e","entryId":"0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f","expectedEntryRevision":"+7"}}""" },
        { "zero expected revision", """{"target":{"kind":"update","vaultId":"0e0e0e0e-0e0e-4e0e-8e0e-0e0e0e0e0e0e","entryId":"0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f","expectedEntryRevision":"0"}}""" },
        { "u64 overflow", """{"approverManifestRevision":"18446744073709551616"}""" },
        { "uppercase submission id", """{"submissionId":"0D0D0D0D-0D0D-4D0D-8D0D-0D0D0D0D0D0D"}""" },
        { "zero agent key version", """{"agentKeyVersion":0}""" },
        { "zero approver key version", """{"approverKeyVersion":0}""" },
        { "short fingerprint", """{"agentKeyFingerprint":"AAAA"}""" },
        { "short payload", """{"encodedPayload":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""" },
    };

    public static TheoryData<string, string> SubmissionJsonCases => new()
    {
        { "unknown transport field", """{"note":"x"}""" },
        { "string key version", """{"agentKeyVersion":"3"}""" },
        { "target is not an object", """{"target":"update"}""" },
        { "unknown target field", """{"target":{"kind":"new","label":"x"}}""" },
    };

    [Fact]
    public void When_ProtocolSubmissionIsParsed_Then_TheCanonicalVectorIsAccepted()
    {
        // Given
        var signed = Resign(JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject());

        // When
        var parsed = AgentSubmissionCrypto.ParseSubmission(signed.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web));

        // Then
        AgentSubmissionCrypto.VerifyAgentSignature(parsed, Fixture.AgentEd25519PublicKey).ShouldBeTrue();
        signed["agentSignature"]!.GetValue<string>().ShouldBe(Fixture.Submission(0).AgentSignature);
    }

    [Theory]
    [MemberData(nameof(SubmissionShapeCases))]
    public void When_ResignedSubmissionBreaksWireEncoding_Then_ShapeValidationRejectsIt(string name, string change)
    {
        // Given
        var candidate = Resign(Merge(JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject(), change));
        var contract = candidate.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web);

        // When
        var exception = Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseSubmission(contract), name);

        // Then
        exception.Code.ShouldBe("invalid-input", name);
    }

    [Theory]
    [MemberData(nameof(SubmissionJsonCases))]
    public void When_ResignedSubmissionBreaksTheClosedJsonShape_Then_DeserializationRejectsIt(string name, string change)
    {
        // Given
        var candidate = Resign(Merge(JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject(), change));

        // When / Then
        Should.Throw<JsonException>(() => candidate.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web), name);
    }

    [Fact]
    public void When_SubmissionIsPaddedOrTruncated_Then_BinaryEncodingIsRejected()
    {
        // Given
        var source = Fixture.Submission(0);
        var padded = source with { AgentKeyFingerprint = source.AgentKeyFingerprint + "=" };
        var truncated = source with { EncodedApproverWrappedDek = source.EncodedApproverWrappedDek[..^4] };
        var shortSignature = source with { AgentSignature = source.AgentSignature[..^4] };

        // When / Then
        Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseSubmission(shortSignature)).Code.ShouldBe("invalid-input");
        Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseSubmission(padded)).Code.ShouldBe("invalid-input");
        Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseSubmission(truncated)).Code.ShouldBe("invalid-input");
    }

    [Fact]
    public void When_ScriptEntryTypeIsResigned_Then_ItIsUnsupported()
    {
        // Given
        var candidate = Resign(Merge(JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject(), """{"entryType":2}"""));

        // When
        var exception = Should.Throw<AgentSubmissionInputException>(() =>
            AgentSubmissionCrypto.ParseSubmission(candidate.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web)));

        // Then
        exception.Code.ShouldBe("unsupported-entry-type");
    }

    [Fact]
    public void When_SignatureOrUnsignedOrganizationIsChanged_Then_AgentSignatureFails()
    {
        // Given
        var source = Fixture.Submission(0);
        var corrupted = source with { AgentSignature = FlipLastByte(source.AgentSignature) };
        var substituted = source with { OrganizationId = "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a" };

        // When / Then
        AgentSubmissionCrypto.VerifyAgentSignature(AgentSubmissionCrypto.ParseSubmission(corrupted), Fixture.AgentEd25519PublicKey)
            .ShouldBeFalse();
        AgentSubmissionCrypto.VerifyAgentSignature(AgentSubmissionCrypto.ParseSubmission(substituted), Fixture.AgentEd25519PublicKey)
            .ShouldBeFalse();
    }

    public static TheoryData<string, string, string> ManifestCases => new()
    {
        { "unknown manifest schema", """{"schema":"palladin.organization-approver-manifest.v2"}""", "invalid-manifest" },
        { "unknown manifest contract version", """{"contractVersion":2}""", "invalid-manifest" },
        { "unknown manifest protocol version", """{"protocolVersion":3}""", "invalid-manifest" },
        { "missing approver set digest", """{"approverSetDigest":null}""", "invalid-manifest" },
        { "missing issuedAt", """{"issuedAt":null}""", "invalid-manifest" },
        { "malformed issuedAt", """{"issuedAt":"2026-10-29 08:00:00"}""", "invalid-manifest" },
        { "noncanonical manifest revision", """{"manifestRevision":"01"}""", "invalid-manifest" },
        { "zero manifest revision", """{"manifestRevision":"0"}""", "invalid-manifest" },
        { "zero key version", """{"keyVersion":0}""", "invalid-manifest" },
        { "short approver set digest", """{"approverSetDigest":"AAAAAAAAAAAAAAAAAAAA"}""", "invalid-manifest" },
        { "uppercase organization", """{"organizationId":"0A0A0A0A-0A0A-4A0A-8A0A-0A0A0A0A0A0A"}""", "invalid-manifest" },
        { "padded signing key", """{"signingPublicKey":"padded="}""", "invalid-manifest" },
        { "key version one with continuity", """{"previousSigningKeyFingerprint":"__FP__","continuitySignature":"__SIG__"}""", "invalid-manifest" },
    };

    [Theory]
    [MemberData(nameof(ManifestCases))]
    public void When_ResignedManifestBreaksTheContract_Then_ParsingRejectsIt(string name, string change, string code)
    {
        // Given
        var fixtureManifest = JsonNode.Parse(Fixture.ManifestJson(0))!.AsObject();
        change = change
            .Replace("__FP__", fixtureManifest["signingKeyFingerprint"]!.GetValue<string>())
            .Replace("__SIG__", fixtureManifest["signature"]!.GetValue<string>());
        var candidate = ResignManifest(Merge(fixtureManifest, change));

        // When
        var exception = Should.Throw<AgentSubmissionInputException>(() =>
            AgentSubmissionCrypto.ParseManifest(candidate.Deserialize<OrganizationApproverManifestContract>(JsonSerializerOptions.Web)), name);

        // Then
        exception.Code.ShouldBe(code, name);
    }

    [Fact]
    public void When_ManifestHasAnAdditionalField_Then_DeserializationRejectsIt()
    {
        // Given
        var candidate = ResignManifest(Merge(JsonNode.Parse(Fixture.ManifestJson(0))!.AsObject(), """{"note":"x"}"""));

        // When / Then
        Should.Throw<JsonException>(() => candidate.Deserialize<OrganizationApproverManifestContract>(JsonSerializerOptions.Web));
    }

    [Fact]
    public void When_SameVersionAndReBootstrapManifestsAreSignedByTheProtocolKeys_Then_TheyParseWithoutContinuity()
    {
        // Given
        var added = ResignManifest(Merge(JsonNode.Parse(Fixture.ManifestJson(0))!.AsObject(),
            """{"manifestRevision":"2","approverSetRevision":"2"}"""));
        var reBootstrap = JsonNode.Parse(Fixture.ManifestJson(1))!.AsObject();
        reBootstrap.Remove("previousSigningKeyFingerprint");
        reBootstrap.Remove("continuitySignature");
        reBootstrap = SignManifest(reBootstrap, Fixture.Root["deterministicInputs"]!["approverV2Ed25519Seed"]!.GetValue<string>());

        // When
        var parsedAdded = AgentSubmissionCrypto.ParseManifest(added.Deserialize<OrganizationApproverManifestContract>(JsonSerializerOptions.Web));
        var parsedReBootstrap = AgentSubmissionCrypto.ParseManifest(
            reBootstrap.Deserialize<OrganizationApproverManifestContract>(JsonSerializerOptions.Web));

        // Then
        parsedAdded.ManifestRevision.ShouldBe(2UL);
        parsedAdded.PreviousSigningKeyFingerprint.ShouldBeNull();
        parsedReBootstrap.KeyVersion.ShouldBe(2u);
        parsedReBootstrap.PreviousSigningKeyFingerprint.ShouldBeNull();
    }

    private static JsonObject Merge(JsonObject target, string change)
    {
        foreach (var (key, value) in JsonNode.Parse(change)!.AsObject())
        {
            target[key] = value?.DeepClone();
        }

        return target;
    }

    private static JsonObject Resign(JsonObject submission)
    {
        submission.Remove("agentSignature");
        var signature = Sign("PLDNSUB1SIG:AGENT-SUBMISSION:", submission,
            Fixture.Root["deterministicInputs"]!["agentEd25519Seed"]!.GetValue<string>());
        submission["agentSignature"] = signature;
        return submission;
    }

    private static JsonObject ResignManifest(JsonObject manifest) =>
        SignManifest(manifest, Fixture.Root["deterministicInputs"]!["approverV1Ed25519Seed"]!.GetValue<string>());

    private static JsonObject SignManifest(JsonObject manifest, string seedHex)
    {
        var continuity = manifest["continuitySignature"]?.DeepClone();
        manifest.Remove("signature");
        manifest.Remove("continuitySignature");
        foreach (var key in manifest.Where(x => x.Value is null).Select(x => x.Key).ToList())
        {
            manifest.Remove(key);
        }

        manifest["signature"] = Sign("PLDNSUB1SIG:APPROVER-MANIFEST:", manifest, seedHex);
        if (continuity is not null)
        {
            manifest["continuitySignature"] = continuity;
        }

        return manifest;
    }

    private static string Sign(string domain, JsonObject unsigned, string seedHex)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, Convert.FromHexString(seedHex), KeyBlobFormat.RawPrivateKey);
        var prefix = Encoding.ASCII.GetBytes(domain);
        var canonical = Encoding.UTF8.GetBytes(Canonical(unsigned));
        var input = new byte[prefix.Length + 2 + canonical.Length];
        prefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(prefix.Length), 2);
        canonical.CopyTo(input, prefix.Length + 2);
        return WebEncoders.Base64UrlEncode(SignatureAlgorithm.Ed25519.Sign(key, input));
    }

    private static string Canonical(JsonNode node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value!))) + "}",
        _ => node.ToJsonString(),
    };

    private static string FlipLastByte(string value)
    {
        var bytes = WebEncoders.Base64UrlDecode(value);
        bytes[^1] ^= 1;
        return WebEncoders.Base64UrlEncode(bytes);
    }
}
