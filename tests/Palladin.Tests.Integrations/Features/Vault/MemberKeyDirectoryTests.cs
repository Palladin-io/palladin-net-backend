using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Cryptography;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class MemberKeyDirectoryTests(ApiFactory apiFactory) : TestBase
{
    private static readonly string CurrentSuite = KeyWrapperSuites.Current.Id;
    private static readonly string LegacySuite = LegacyWrapperSuite.Id;

    [Fact]
    public async Task When_IdentityPublishesMemberKey_Then_VaultStoresSuiteAndSuiteBoundFingerprint()
    {
        // Given
        var userId = Guid.NewGuid();
        var rawPublicKey = Key(CurrentSuite, 0x01);
        var updatedAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, CurrentSuite, rawPublicKey, updatedAt));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var entry = await readContext.MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)1);
        entry.PublicKeySuiteId.ShouldBe(CurrentSuite);
        entry.Fingerprint.ShouldBe(Fingerprint(CurrentSuite, rawPublicKey));
        entry.Fingerprint.ShouldNotBe(Fingerprint(LegacySuite, rawPublicKey[..32]));
        entry.PublicKey.ShouldBe(rawPublicKey);
    }

    [Fact]
    public async Task When_LegacyMemberKeyIsReplacedByCurrentSuiteGeneration_Then_DirectorySwitchesSuite()
    {
        // Given
        var userId = Guid.NewGuid();
        var at = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, LegacySuite, Key(LegacySuite, 0x31), at));

        // When
        var upgradedKey = Key(CurrentSuite, 0x32);
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 2, CurrentSuite, upgradedKey, at));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)2);
        entry.PublicKeySuiteId.ShouldBe(CurrentSuite);
        entry.Fingerprint.ShouldBe(Fingerprint(CurrentSuite, upgradedKey));
    }

    [Fact]
    public async Task When_KeyLengthDoesNotMatchDeclaredSuite_Then_FailsClosedWithoutDirectoryRow()
    {
        // Given
        var userId = Guid.NewGuid();
        var at = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        // When
        var action = () => ConsumeAsync(new UpsertMemberKeyDirectoryCommand(
            userId, 1, CurrentSuite, Key(LegacySuite, 0x41), at));

        // Then
        await action.ShouldThrowAsync<Palladin.Core.Types.Exceptions.DomainException>();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.AnyAsync(x => x.UserId == userId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_OlderMemberKeyEventArrives_Then_DoesNotRollDirectoryBack()
    {
        var userId = Guid.NewGuid();
        var currentKey = Key(CurrentSuite, 0xA5);
        var staleKey = Key(LegacySuite, 0x5A);
        var currentAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant()) + Duration.FromSeconds(1);
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 2, CurrentSuite, currentKey, currentAt));

        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(
            userId,
            1,
            LegacySuite,
            staleKey,
            currentAt - Duration.FromSeconds(1)));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)2);
        entry.Fingerprint.ShouldBe(Fingerprint(CurrentSuite, currentKey));
    }

    [Fact]
    public async Task When_FingerprintChangesWithoutVersionIncrement_Then_FailsClosed()
    {
        var userId = Guid.NewGuid();
        var firstKey = Key(CurrentSuite, 0x11);
        var substitutedKey = Key(CurrentSuite, 0x22);
        var firstAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, CurrentSuite, firstKey, firstAt));

        var action = () => ConsumeAsync(new UpsertMemberKeyDirectoryCommand(
            userId,
            1,
            CurrentSuite,
            substitutedKey,
            firstAt + Duration.FromSeconds(1)));

        await action.ShouldThrowAsync<Palladin.Core.Types.Exceptions.DomainException>();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.Fingerprint.ShouldBe(Fingerprint(CurrentSuite, firstKey));
    }

    [Fact]
    public async Task When_HigherVersionHasEqualTimestamp_Then_VersionRemainsPrimaryOrdering()
    {
        var userId = Guid.NewGuid();
        var firstKey = Key(CurrentSuite, 0x11);
        var rotatedKey = Key(CurrentSuite, 0x22);
        var timestamp = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, CurrentSuite, firstKey, timestamp));

        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 2, CurrentSuite, rotatedKey, timestamp));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)2);
        entry.Fingerprint.ShouldBe(Fingerprint(CurrentSuite, rotatedKey));
    }

    private async Task ConsumeAsync(UpsertMemberKeyDirectoryCommand command)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new UpsertMemberKeyDirectoryConsumer(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());

        await consumer.Consume(apiFactory.MockConsumeContext(command));
    }

    private static byte[] Key(string suiteId, byte fill) =>
        Enumerable.Repeat(fill, KeyWrapperSuites.Resolve(suiteId).PublicKeyBytes).ToArray();

    private static byte[] Fingerprint(string suiteId, byte[] key) =>
        RecipientKeyFingerprint.Compute(suiteId, VaultKeyKind.MemberEncryption, key);

    private static Instant TruncateToMs(Instant instant) =>
        Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
}
