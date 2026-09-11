using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class SharedUnlockApiContractTests
{
    [Fact]
    public void When_SerializingEveryCandidateResponseVariant_Then_WireShapeAndNullableValuesMatch()
    {
        // Given
        using var fixture = Read();
        var options = PalladinJsonSerializationSettings.DefaultOptions;
        foreach (var vector in fixture.RootElement.GetProperty("responses").EnumerateArray())
        {
            var type = vector.GetProperty("type").GetString() switch
            {
                "preference" => typeof(SharedUnlockPreferenceResponse),
                "link" => typeof(SharedUnlockLinkResponse),
                "authorization" => typeof(AuthorizeSharedUnlockResponse),
                "operation" => typeof(SharedUnlockOperationResponse),
                "commit" => typeof(CommitSharedUnlockOperationResponse),
                _ => throw new InvalidOperationException(),
            };
            var expected = vector.GetProperty("body");

            // When
            var response = expected.Deserialize(type, options)!;
            var serialized = JsonSerializer.SerializeToNode(response, type, options);

            // Then
            JsonNode.DeepEquals(serialized, JsonNode.Parse(expected.GetRawText()))
                .ShouldBeTrue(vector.GetProperty("name").GetString());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void When_ReadingOperationRequestFixture_Then_RequiredCeilingsAndWireShapeMatch(int index)
    {
        // Given
        using var fixture = Read();
        var expected = fixture.RootElement.GetProperty("operations")[index].GetProperty("createRequest");
        var options = PalladinJsonSerializationSettings.DefaultOptions;

        // When
        var request = expected.Deserialize<CreateSharedUnlockOperationRequest>(options)!;
        var serialized = JsonSerializer.SerializeToNode(request, options);
        var validator = new CreateSharedUnlockOperationValidator();

        // Then
        JsonNode.DeepEquals(serialized, JsonNode.Parse(expected.GetRawText())).ShouldBeTrue();
        validator.Validate(request).IsValid.ShouldBeTrue();
        foreach (var invalid in new[] { 0L, -1L, long.MaxValue })
        {
            validator.Validate(request with { IdleDeadlineMs = invalid }).IsValid.ShouldBeFalse();
            validator.Validate(request with { AbsoluteDeadlineMs = invalid }).IsValid.ShouldBeFalse();
            validator.Validate(request with { OfflineDeadlineMs = invalid }).IsValid.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void When_ApiDescriptorAndOperationProofsAreCombined_Then_IndependentBindingsMatch(int index)
    {
        // Given
        using var fixture = Read();
        var vector = fixture.RootElement.GetProperty("operations")[index];
        var operation = vector.GetProperty("operation").Deserialize<SharedUnlockOperationResponse>(PalladinJsonSerializationSettings.DefaultOptions)!;
        var context = operation.Context;
        var proof = new SharedUnlockProofAuthority(context.OperationId, Decode(operation.Challenge),
            Decode(operation.TranscriptHash), Decode(operation.RecipientProofPublicKey),
            Instant.FromUnixTimeMilliseconds(context.IssuedAtMs), Instant.FromUnixTimeMilliseconds(context.ExpiresAtMs));
        var consume = vector.GetProperty("proofs").GetProperty("consume")
            .Deserialize<ConsumeSharedUnlockOperationRequest>(PalladinJsonSerializationSettings.DefaultOptions)!;
        var commit = vector.GetProperty("proofs").GetProperty("commit")
            .Deserialize<CommitSharedUnlockOperationRequest>(PalladinJsonSerializationSettings.DefaultOptions)!;
        var now = proof.IssuedAt.Plus(Duration.FromMilliseconds(1));

        // When
        var digest = SharedUnlockKeyContextDigest.Hash(operation.KeyContext);
        var transcriptHash = SHA256.HashData(Decode(vector.GetProperty("transcript").GetString()!));

        // Then
        operation.KeyContext.AccountId.ShouldBe(context.AccountId);
        operation.KeyContext.KdfProfileId.ShouldBe(IdentityKdfProfiles.CurrentProfileId);
        operation.KeyContext.SecurityVersion.ShouldBe(IdentityKdfProfiles.CurrentSecurityVersion);
        operation.KeyContext.MinimumSecurityVersion.ShouldBe(IdentityKdfProfiles.CurrentSecurityVersion);
        Decode(operation.KeyContext.KdfSalt).Length.ShouldBe(IdentityKdfProfiles.KdfSaltBytes);
        context.OperationId.ShouldNotBe(context.AccountId);
        var source = vector.GetProperty("sourceAuthorization").Deserialize<AuthorizeSharedUnlockResponse>(PalladinJsonSerializationSettings.DefaultOptions)!;
        if (index == 1)
        {
            source.OrganizationId.ShouldNotBe(context.OrganizationId);
            source.AuthorizationVersion.ShouldNotBe(context.AuthorizationVersion);
        }
        else
        {
            source.OrganizationId.ShouldBe(context.OrganizationId);
            source.AuthorizationVersion.ShouldBe(context.AuthorizationVersion);
        }
        digest.ShouldBe(Decode(context.KeyContextDigest));
        transcriptHash.ShouldBe(proof.TranscriptHash);
        consume.OperationId.ShouldBe(context.OperationId);
        commit.OperationId.ShouldBe(context.OperationId);
        SharedUnlockIdentityProof.Verify(proof, SharedUnlockProofPurpose.Consume, consume.Signature, now).ShouldBeTrue();
        SharedUnlockIdentityProof.Verify(proof, SharedUnlockProofPurpose.Commit, commit.Signature, now).ShouldBeTrue();
        SharedUnlockIdentityProof.Verify(proof, SharedUnlockProofPurpose.Commit, consume.Signature, now).ShouldBeFalse();
        SharedUnlockIdentityProof.Verify(proof, SharedUnlockProofPurpose.Consume, commit.Signature, now).ShouldBeFalse();
    }

    [Fact]
    public void When_LinkFollowsSharedLifecycleFixture_Then_LogoutBarrierSurvivesReconnectAndFreshUnlock()
    {
        // Given
        using var fixture = Read();
        var responses = fixture.RootElement.GetProperty("responses").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "link")
            .Select(item => item.GetProperty("body").Deserialize<SharedUnlockLinkResponse>(PalladinJsonSerializationSettings.DefaultOptions)!)
            .ToArray();
        var lifecycle = fixture.RootElement.GetProperty("lifecycle");
        var sequence = lifecycle.GetProperty("originalSequence").GetUInt32();
        var freshSequence = lifecycle.GetProperty("freshManualSequence").GetUInt32();
        var now = Instant.FromUnixTimeSeconds(1800000000);
        var account = fixture.RootElement.GetProperty("operations")[0].GetProperty("operation").GetProperty("context").GetProperty("accountId").GetGuid();
        var link = SharedUnlockLink.Create(account, responses[0].LinkId, now);
        Action[] transitions =
        [
            () => { },
            () => link.TryActivateFromManualUnlock(link.Revision, sequence, now).ShouldBeTrue(),
            () => link.TryLock(link.Revision, sequence + 1, now).ShouldBeTrue(),
            () => link.TryLogout(link.Revision, sequence + 2, now).ShouldBeTrue(),
            () => link.TryDisconnect(link.Revision, sequence + 3, now).ShouldBeTrue(),
            () => link.TryReconnect(link.Revision, sequence + 4, now).ShouldBeTrue(),
            () => link.TryActivateFromManualUnlock(link.Revision, freshSequence, now).ShouldBeTrue(),
        ];
        for (var index = 0; index < transitions.Length; index++)
        {
            // When
            transitions[index]();
            var actual = new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch,
                link.State.ToString().ToLowerInvariant(), link.LastInvalidationSequence, link.LastLogoutSequence);

            // Then
            actual.ShouldBe(responses[index]);
            if (index >= 3)
            {
                link.LastLogoutSequence.ShouldBeGreaterThanOrEqualTo(sequence);
                freshSequence.ShouldBeGreaterThan(link.LastLogoutSequence);
            }
        }
    }

    private static byte[] Decode(string value) => Base64Url.DecodeFromChars(value);
    private static JsonDocument Read() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SharedUnlock", "session-api-v1.json")));
}
