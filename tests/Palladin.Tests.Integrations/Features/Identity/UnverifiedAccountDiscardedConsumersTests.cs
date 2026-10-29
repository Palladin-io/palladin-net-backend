using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;
using AgentsTriggers = Palladin.Module.Agents.Triggers;
using NotificationTriggers = Palladin.Module.Notification.Triggers;
using VaultTriggers = Palladin.Module.Vault.Triggers;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class UnverifiedAccountDiscardedConsumersTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UnverifiedAccountIsDiscarded_Then_EveryModuleDropsItsUserReplicas()
    {
        // Given
        var (userId, organizationId) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedReplicasAsync(userId, organizationId);

        // When
        await DiscardAsync(userId, organizationId);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await vault.Users.AnyAsync(u => u.Id == userId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await vault.MemberKeyDirectory.AnyAsync(e => e.UserId == userId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>().Users
            .AnyAsync(u => u.Id == userId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        var notification = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await notification.Users.AnyAsync(u => u.UserId == userId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await notification.Scopes.AnyAsync(s => s.UserId == userId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_DiscardedUserIsStillAVaultMember_Then_VaultKeepsItsDirectoryKey()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        await ConsumeAsync<VaultTriggers.OnUserUpserted, UserUpsertedEvent>(Upserted(owner.Id, organization.Id));
        await ConsumeAsync<UpsertMemberKeyDirectoryConsumer, UpsertMemberKeyDirectoryCommand>(
            new UpsertMemberKeyDirectoryCommand(owner.Id, 1, Enumerable.Repeat((byte)7, 32).ToArray(), Now()));

        // When
        await ConsumeAsync<VaultTriggers.OnUnverifiedAccountDiscarded, UnverifiedAccountDiscardedEvent>(
            new UnverifiedAccountDiscardedEvent(owner.Id, organization.Id, Now()));

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().MemberKeyDirectory
            .AnyAsync(e => e.UserId == owner.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private async Task SeedReplicasAsync(Guid userId, Guid organizationId)
    {
        await ConsumeAsync<VaultTriggers.OnUserUpserted, UserUpsertedEvent>(Upserted(userId, organizationId));
        await ConsumeAsync<UpsertMemberKeyDirectoryConsumer, UpsertMemberKeyDirectoryCommand>(
            new UpsertMemberKeyDirectoryCommand(userId, 1, Enumerable.Repeat((byte)9, 32).ToArray(), Now()));
        await ConsumeAsync<AgentsTriggers.OnUserUpserted, UserUpsertedEvent>(Upserted(userId, organizationId));
        await ConsumeAsync<NotificationTriggers.OnUserUpserted, UserUpsertedEvent>(Upserted(userId, organizationId));
    }

    private async Task DiscardAsync(Guid userId, Guid organizationId)
    {
        var discarded = new UnverifiedAccountDiscardedEvent(userId, organizationId, Now());
        await ConsumeAsync<VaultTriggers.OnUnverifiedAccountDiscarded, UnverifiedAccountDiscardedEvent>(discarded);
        await ConsumeAsync<AgentsTriggers.OnUnverifiedAccountDiscarded, UnverifiedAccountDiscardedEvent>(discarded);
        await ConsumeAsync<NotificationTriggers.OnUnverifiedAccountDiscarded, UnverifiedAccountDiscardedEvent>(discarded);
    }

    private UserUpsertedEvent Upserted(Guid userId, Guid organizationId) =>
        new(userId, organizationId, "Squatter", $"{userId:N}@example.com", Permission.None, Now());

    private Instant Now() =>
        Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());

    private async Task ConsumeAsync<TConsumer, TMessage>(TMessage message)
        where TConsumer : IConsumer<TMessage>
        where TMessage : class
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<TConsumer>(scope.ServiceProvider)
            .Consume(apiFactory.MockConsumeContext(message));
    }
}
