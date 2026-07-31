using Palladin.Core.Analytics;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using MassTransit;
using NodaTime;
using NSubstitute;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class OnVaultCreatedTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_NormalVaultCreated_Then_FiresVaultCreatedAnalyticsAndScopeUpdate()
    {
        // Given
        var (analytics, publishEndpoint) = (Substitute.For<IAnalyticsService>(), Substitute.For<IPublishEndpoint>());
        var userId = Guid.NewGuid();
        var evt = Upserted(userId, isDefault: false, EntityChange.Created);

        // When
        await new OnVaultUpserted(analytics, publishEndpoint).Consume(apiFactory.MockConsumeContext(evt));

        // Then
        analytics.Received(1).CaptureEvent(userId.ToString(), "vault", "vault-created");
        await publishEndpoint.Received(1).Publish(Arg.Any<UpdateUserScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_DefaultVaultCreated_Then_SkipsAnalyticsButStillUpdatesScope()
    {
        // Given — the auto-created default vault must not inflate the vault-created funnel
        var (analytics, publishEndpoint) = (Substitute.For<IAnalyticsService>(), Substitute.For<IPublishEndpoint>());
        var evt = Upserted(Guid.NewGuid(), isDefault: true, EntityChange.Created);

        // When
        await new OnVaultUpserted(analytics, publishEndpoint).Consume(apiFactory.MockConsumeContext(evt));

        // Then — no funnel event, but scope update still runs
        analytics.DidNotReceive().CaptureEvent(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await publishEndpoint.Received(1).Publish(Arg.Any<UpdateUserScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_VaultUpdated_Then_FiresVaultUpdatedAnalyticsAndNoScopeUpdate()
    {
        // Given
        var (analytics, publishEndpoint) = (Substitute.For<IAnalyticsService>(), Substitute.For<IPublishEndpoint>());
        var userId = Guid.NewGuid();
        var evt = new VaultUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), userId, "Owner", false,
            EntityChange.Updated, apiFactory.FakeClock.GetCurrentInstant());

        // When
        await new OnVaultUpserted(analytics, publishEndpoint).Consume(apiFactory.MockConsumeContext(evt));

        // Then — the update funnel fires; scope updates are a creation-only concern
        analytics.Received(1).CaptureEvent(userId.ToString(), "vault", "vault-updated");
        await publishEndpoint.DidNotReceive().Publish(Arg.Any<UpdateUserScope>(), Arg.Any<CancellationToken>());
    }

    private VaultUpsertedEvent Upserted(Guid userId, bool isDefault, EntityChange change) =>
        new(Guid.NewGuid(), Guid.NewGuid(), userId, "Owner", isDefault, change,
            apiFactory.FakeClock.GetCurrentInstant());
}
