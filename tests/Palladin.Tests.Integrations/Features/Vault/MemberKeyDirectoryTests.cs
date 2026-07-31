using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
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
    [Fact]
    public async Task When_IdentityPublishesMemberKey_Then_VaultStoresOnlyAuthenticatedFingerprint()
    {
        var userId = Guid.NewGuid();
        var rawPublicKey = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var updatedAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, rawPublicKey, updatedAt));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var entry = await readContext.MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)1);
        entry.Fingerprint.ShouldBe(MemberKeyFingerprint.Compute(rawPublicKey));
        entry.Fingerprint.ShouldNotBe(rawPublicKey);
        entry.PublicKey.ShouldBe(rawPublicKey);
    }

    [Fact]
    public async Task When_OlderMemberKeyEventArrives_Then_DoesNotRollDirectoryBack()
    {
        var userId = Guid.NewGuid();
        var currentKey = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var staleKey = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var currentAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant()) + Duration.FromSeconds(1);
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 2, currentKey, currentAt));

        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(
            userId,
            1,
            staleKey,
            currentAt - Duration.FromSeconds(1)));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)2);
        entry.Fingerprint.ShouldBe(MemberKeyFingerprint.Compute(currentKey));
    }

    [Fact]
    public async Task When_FingerprintChangesWithoutVersionIncrement_Then_FailsClosed()
    {
        var userId = Guid.NewGuid();
        var firstKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var substitutedKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var firstAt = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, firstKey, firstAt));

        var action = () => ConsumeAsync(new UpsertMemberKeyDirectoryCommand(
            userId,
            1,
            substitutedKey,
            firstAt + Duration.FromSeconds(1)));

        await action.ShouldThrowAsync<Palladin.Core.Types.Exceptions.DomainException>();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.Fingerprint.ShouldBe(MemberKeyFingerprint.Compute(firstKey));
    }

    [Fact]
    public async Task When_HigherVersionHasEqualTimestamp_Then_VersionRemainsPrimaryOrdering()
    {
        var userId = Guid.NewGuid();
        var firstKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var rotatedKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var timestamp = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 1, firstKey, timestamp));

        await ConsumeAsync(new UpsertMemberKeyDirectoryCommand(userId, 2, rotatedKey, timestamp));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == userId);
        entry.KeyVersion.Value.ShouldBe((uint)2);
        entry.Fingerprint.ShouldBe(MemberKeyFingerprint.Compute(rotatedKey));
    }

    private async Task ConsumeAsync(UpsertMemberKeyDirectoryCommand command)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new UpsertMemberKeyDirectoryConsumer(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());

        await consumer.Consume(apiFactory.MockConsumeContext(command));
    }

    private static Instant TruncateToMs(Instant instant) =>
        Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
}
