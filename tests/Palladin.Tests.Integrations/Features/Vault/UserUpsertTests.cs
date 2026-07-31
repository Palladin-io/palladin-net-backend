using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class UserUpsertTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserUpsertedAndReplicaMissing_Then_CreatesUser()
    {
        // Given
        var userId = Guid.NewGuid();
        var now = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Alice Approver", "user@test.io", Permission.None, now));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        replica.ShouldNotBeNull();
        replica.DisplayName.ShouldBe("Alice Approver");
        replica.UpdatedAt.ShouldBe(now);
    }

    [Fact]
    public async Task When_UserUpsertedWithNewerTimestamp_Then_UpdatesReplica()
    {
        // Given
        var userId = Guid.NewGuid();
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Old Name", "user@test.io", Permission.None, t0));

        // When
        var t1 = t0 + Duration.FromSeconds(1);
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "New Name", "user@test.io", Permission.None, t1));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Users.FirstAsync(u => u.Id == userId);
        replica.DisplayName.ShouldBe("New Name");
        replica.UpdatedAt.ShouldBe(t1);
    }

    [Fact]
    public async Task When_UserUpsertedWithOlderTimestamp_Then_Ignored()
    {
        // Given
        var userId = Guid.NewGuid();
        var t1 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant()) + Duration.FromSeconds(10);
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Current", "user@test.io", Permission.None, t1));

        // When
        var t0 = apiFactory.FakeClock.GetCurrentInstant();
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Stale", "user@test.io", Permission.None, t0));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var replica = await readContext.Users.FirstAsync(u => u.Id == userId);
        replica.DisplayName.ShouldBe("Current");
    }

    private async Task ConsumeAsync(UserUpsertedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnUserUpserted(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }

    private static Instant TruncateToMs(Instant instant) =>
        Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
}
