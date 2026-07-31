using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class OnUserUpsertedTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UserDoesNotExist_Then_CreatesUser()
    {
        // Given
        var userId = Guid.NewGuid();
        var now = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());

        // When
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Ada Lovelace", "user@test.io", Permission.None, now));

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        persisted.ShouldNotBeNull();
        persisted.DisplayName.ShouldBe("Ada Lovelace");
        persisted.UpdatedAt.ShouldBe(now);
    }

    [Fact]
    public async Task When_UserAlreadyExistsAndEventIsNewer_Then_UpdatesDisplayName()
    {
        // Given
        var userId = Guid.NewGuid();
        var t0 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant());
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Original Name", "user@test.io", Permission.None, t0));

        // When
        var t1 = t0 + Duration.FromSeconds(1);
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Updated Name", "user@test.io", Permission.None, t1));

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        persisted.ShouldNotBeNull();
        persisted.DisplayName.ShouldBe("Updated Name");
        persisted.UpdatedAt.ShouldBe(t1);
    }

    [Fact]
    public async Task When_UserAlreadyExistsAndEventIsOlder_Then_IgnoresUpdate()
    {
        // Given
        var userId = Guid.NewGuid();
        var t1 = TruncateToMs(apiFactory.FakeClock.GetCurrentInstant()) + Duration.FromSeconds(10);
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Current Name", "user@test.io", Permission.None, t1));

        // When
        var t0 = apiFactory.FakeClock.GetCurrentInstant();
        await ConsumeAsync(new UserUpsertedEvent(userId, Guid.NewGuid(), "Stale Name", "user@test.io", Permission.None, t0));

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var persisted = await readContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
        persisted.ShouldNotBeNull();
        persisted.DisplayName.ShouldBe("Current Name");
    }

    private async Task ConsumeAsync(UserUpsertedEvent @event)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnUserUpserted(
            scope.ServiceProvider.GetRequiredService<AgentsDomainWriteContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await consumer.Consume(apiFactory.MockConsumeContext(@event));
    }

    private static Instant TruncateToMs(Instant instant) =>
        Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
}
