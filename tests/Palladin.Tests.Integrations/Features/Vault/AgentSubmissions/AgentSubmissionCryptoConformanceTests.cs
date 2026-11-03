using System.Text.Json;
using System.Text.Json.Nodes;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

public sealed class AgentSubmissionCryptoConformanceTests
{
    private static AgentSubmissionFixture Fixture => AgentSubmissionFixture.Current;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void When_ProtocolSubmissionIsVerified_Then_SignatureAndCanonicalDocumentMatchByteForByte(int index)
    {
        // Given
        var contract = Fixture.Submission(index);

        // When
        var parsed = AgentSubmissionCrypto.ParseSubmission(contract);

        // Then
        AgentSubmissionCrypto.VerifyAgentSignature(parsed, Fixture.AgentEd25519PublicKey).ShouldBeTrue();
        parsed.Document.ShouldBe(Canonical(JsonNode.Parse(Fixture.SubmissionJson(index))!));
        parsed.OrganizationId.ShouldBe(Fixture.OrganizationId);
        parsed.AgentId.ShouldBe(Fixture.AgentId);
        parsed.AgentKeyFingerprint.ShouldBe(VaultKeyFingerprint.Compute(Fixture.AgentX25519PublicKey, VaultKeyKind.AgentX25519));
        parsed.SubmissionRevision.ShouldBe((ulong)index + 1);
    }

    [Fact]
    public void When_RewrappedSubmissionIsCompared_Then_IdentityAndPayloadAreUnchanged()
    {
        // Given
        var first = AgentSubmissionCrypto.ParseSubmission(Fixture.Submission(0));

        // When
        var second = AgentSubmissionCrypto.ParseSubmission(Fixture.Submission(1));

        // Then
        second.IdentityDocument.ShouldBe(first.IdentityDocument);
        second.EncodedPayload.ShouldBe(first.EncodedPayload);
        second.ApproverKeyVersion.ShouldBe(first.ApproverKeyVersion + 1);
    }

    [Theory]
    [InlineData("organizationId", "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a")]
    [InlineData("submissionRevision", "2")]
    [InlineData("approverManifestRevision", "9")]
    [InlineData("encodedPayload", null)]
    public void When_SignedFieldIsSubstituted_Then_AgentSignatureFails(string field, string? replacement)
    {
        // Given
        var node = JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject();
        node[field] = replacement ?? FlipLastByte(node[field]!.GetValue<string>());
        var contract = node.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web);

        // When
        var parsed = AgentSubmissionCrypto.ParseSubmission(contract);

        // Then
        AgentSubmissionCrypto.VerifyAgentSignature(parsed, Fixture.AgentEd25519PublicKey).ShouldBeFalse();
    }

    [Fact]
    public void When_ScriptEntryTypeIsSubmitted_Then_ItIsRejectedAsUnsupported()
    {
        // Given
        var contract = Fixture.Submission(0) with { EntryType = 2 };

        // When
        var exception = Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseSubmission(contract));

        // Then
        exception.Code.ShouldBe("unsupported-entry-type");
    }

    [Fact]
    public void When_UnknownTransportFieldIsPresent_Then_DeserializationFails()
    {
        // Given
        var node = JsonNode.Parse(Fixture.SubmissionJson(0))!.AsObject();
        node["note"] = "x";

        // When / Then
        Should.Throw<JsonException>(() => node.Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web));
    }

    [Fact]
    public void When_ProtocolManifestChainIsVerified_Then_SelfContinuityFingerprintsAndDigestMatch()
    {
        // Given
        var first = Fixture.Manifest(0);
        var second = Fixture.Manifest(1);

        // When
        var parsedFirst = AgentSubmissionCrypto.ParseManifest(first);
        var parsedSecond = AgentSubmissionCrypto.ParseManifest(second);

        // Then
        parsedFirst.Document.ShouldBe(Canonical(JsonNode.Parse(Fixture.ManifestJson(0))!));
        parsedSecond.Document.ShouldBe(Canonical(JsonNode.Parse(Fixture.ManifestJson(1))!));
        AgentSubmissionCrypto.VerifyContinuity(second, parsedFirst.SigningPublicKey).ShouldBeTrue();
        parsedSecond.PreviousSigningKeyFingerprint.ShouldBe(parsedFirst.SigningKeyFingerprint);
        var memberFingerprint = MemberKeyFingerprint.Compute(Fixture.MemberX25519PublicKey);
        AgentSubmissionCrypto.ComputeApproverSetDigest([(Fixture.MemberId, Fixture.MemberKeyVersion, memberFingerprint)])
            .ShouldBe(parsedFirst.ApproverSetDigest);
    }

    [Fact]
    public void When_ManifestSignatureIsCorrupted_Then_ParsingFailsClosed()
    {
        // Given
        var manifest = Fixture.Manifest(0);
        manifest = manifest with { Signature = FlipLastByte(manifest.Signature) };

        // When
        var exception = Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseManifest(manifest));

        // Then
        exception.Code.ShouldBe("manifest-signature-invalid");
    }

    [Fact]
    public void When_ContinuityIsSignedByAnotherKey_Then_ContinuityVerificationFails()
    {
        // Given
        var second = Fixture.Manifest(1);
        var unrelated = AgentSubmissionCrypto.ParseManifest(Fixture.Manifest(1)).SigningPublicKey;

        // When
        var verified = AgentSubmissionCrypto.VerifyContinuity(second, unrelated);

        // Then
        verified.ShouldBeFalse();
    }

    [Fact]
    public void When_RotatedManifestOmitsContinuity_Then_ParsingFailsClosed()
    {
        // Given
        var manifest = Fixture.Manifest(1) with { ContinuitySignature = null };

        // When / Then
        Should.Throw<AgentSubmissionInputException>(() => AgentSubmissionCrypto.ParseManifest(manifest))
            .Code.ShouldBe("invalid-manifest");
    }

    private static string Canonical(JsonNode node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value!))) + "}",
        _ => node.ToJsonString(),
    };

    private static string FlipLastByte(string value)
    {
        var bytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(value);
        bytes[^1] ^= 1;
        return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes);
    }
}
