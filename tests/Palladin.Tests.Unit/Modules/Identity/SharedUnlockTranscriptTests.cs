using System.Buffers.Text;
using System.Text.Json;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Module.Identity.Shared;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class SharedUnlockTranscriptTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void When_OperationMatchesIndependentNodeFixture_Then_CompleteTranscriptMatchesByteForByte(int index)
    {
        // Given
        using var fixture = Read("transport-v1.json");
        var vector = fixture.RootElement.GetProperty("positive")[index];
        var envelope = vector.GetProperty("envelope");
        var context = envelope.GetProperty("context");
        var operation = (SharedUnlockOperation)Activator.CreateInstance(typeof(SharedUnlockOperation), nonPublic: true)!;
        foreach (var (property, field) in new[]
        {
            ("Id", "operationId"), ("UserId", "accountId"), ("OrganizationId", "organizationId"), ("LinkId", "linkId"),
        })
        {
            Set(operation, property, context.GetProperty(field).GetGuid());
        }
        foreach (var (property, field) in new[]
        {
            ("ApiOrigin", "apiOrigin"), ("WebOrigin", "webOrigin"), ("ExtensionId", "extensionId"), ("DocumentBinding", "documentBinding"),
        })
        {
            Set(operation, property, context.GetProperty(field).GetString()!);
        }
        foreach (var (property, field) in new[]
        {
            ("WebGeneration", "webGeneration"), ("ExtensionGeneration", "extensionGeneration"), ("KeyContextDigest", "keyContextDigest"),
        })
        {
            Set(operation, property, Decode(context.GetProperty(field).GetString()!));
        }
        foreach (var (property, field) in new[]
        {
            ("LinkEpoch", "linkEpoch"), ("PreferenceRevision", "preferenceRevision"), ("AuthorizationVersion", "authorizationVersion"),
        })
        {
            Set(operation, property, context.GetProperty(field).GetUInt32());
        }
        foreach (var (property, field) in new[]
        {
            ("IssuedAt", "issuedAtMs"), ("ExpiresAt", "expiresAtMs"), ("UnlockedAt", "unlockedAtMs"),
            ("IdleDeadline", "idleDeadlineMs"), ("AbsoluteDeadline", "absoluteDeadlineMs"), ("OfflineDeadline", "offlineDeadlineMs"),
        })
        {
            Set(operation, property, Instant.FromUnixTimeMilliseconds(context.GetProperty(field).GetInt64()));
        }
        Set(operation, "Direction", context.GetProperty("direction").GetString() == "web-to-extension"
            ? SharedUnlockDirection.WebToExtension : SharedUnlockDirection.ExtensionToWeb);
        Set(operation, "SourcePublicKey", Decode(envelope.GetProperty("sourcePublicKey").GetString()!));
        Set(operation, "RecipientPublicKey", Decode(envelope.GetProperty("recipientPublicKey").GetString()!));

        // When
        var transcript = SharedUnlockTranscript.Encode(operation);

        // Then
        transcript.ShouldBe(Decode(vector.GetProperty("transcript").GetString()!));
    }

    [Fact]
    public void When_KeyContextMatchesIndependentNodeFixture_Then_DigestAndEveryFieldSubstitutionMatch()
    {
        // Given
        using var fixture = Read("key-context-v1.json");
        var positive = fixture.RootElement.GetProperty("positive")[0];
        var descriptor = positive.GetProperty("context").Deserialize<SharedUnlockKeyContext>(JsonSerializerOptions.Web)!;

        // When
        var hash = SharedUnlockKeyContextDigest.Hash(descriptor);

        // Then
        hash.ShouldBe(Decode(positive.GetProperty("digest").GetString()!));
        foreach (var substitution in fixture.RootElement.GetProperty("substitutions").EnumerateArray())
        {
            var changed = substitution.GetProperty("context").Deserialize<SharedUnlockKeyContext>(JsonSerializerOptions.Web)!;
            var changedHash = SharedUnlockKeyContextDigest.Hash(changed);
            changedHash.ShouldNotBe(hash);
            changedHash.ShouldBe(Decode(substitution.GetProperty("digest").GetString()!));
        }
    }

    private static void Set(SharedUnlockOperation operation, string property, object value) =>
        typeof(SharedUnlockOperation).GetProperty(property)!.SetValue(operation, value);
    private static byte[] Decode(string value) => Base64Url.DecodeFromChars(value);
    private static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SharedUnlock", name)));
}
