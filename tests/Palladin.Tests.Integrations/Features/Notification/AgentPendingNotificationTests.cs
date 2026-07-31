using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class AgentPendingNotificationTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentEnrolledPending_Then_BroadcastToOrgScopeReachesAgentManagers()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var manager = Guid.NewGuid();
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, manager, Permission.AgentManage);

        var agentId = Guid.NewGuid();
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            agentId, organizationId, AgentStatus.Pending, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 0, null, Now()));

        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.AgentPending);
        command.Metadata["agentId"].ShouldBe(agentId.ToString());
        command.Metadata["agentType"].ShouldBe("ci");
        command.Metadata["agentPublicKey"].ShouldBe("pk");
        command.Scopes.ShouldBeEmpty();
        command.RequiredPermission.ShouldBe(Permission.AgentManage);

        // When
        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.InboxItems.AnyAsync(i =>
            i.OrganizationId == organizationId && i.UserId == manager && i.Type == NotificationType.AgentPending))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task When_AgentUpsertedActive_Then_NoCommandEmitted()
    {
        // Given
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Active, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 1, Now(), Now()));

        // Then
        command.ShouldBeNull();
    }

    [Fact]
    public async Task When_AgentHasConnectionInfo_Then_AgentPendingMetadataIncludesHostAndIp()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var agentId = Guid.NewGuid();
        await apiFactory.Services.SeedAgentAsync(organization.Id, AgentFaker.Create(id: agentId, organizationId: organization.Id)
            .RuleFor(x => x.Status, AgentStatus.Pending)
            .RuleFor(x => x.AccessEpoch, 0u)
            .RuleFor(x => x.LastHostname, "ci-runner-07")
            .RuleFor(x => x.LastIp, "203.0.113.42"));

        // When
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            agentId, organization.Id, AgentStatus.Pending, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 0, null, Now()));

        // Then
        command.ShouldNotBeNull();
        command.Metadata["agentId"].ShouldBe(agentId.ToString());
        command.Metadata["host"].ShouldBe("ci-runner-07");
        command.Metadata["ip"].ShouldBe("203.0.113.42");
        command.Metadata.ShouldNotContainKey("publicKey");
    }

    [Fact]
    public async Task When_AgentHasIconKeyAndColor_Then_AgentPendingMetadataIncludesBoth()
    {
        // Given
        // When
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Pending, "pk", 1, "signing-pk", "deploy-bot", "ci", "smart_toy", "#FF4F4F", 0, null, Now()));

        // Then
        command.ShouldNotBeNull();
        command.Metadata["agentIconKey"].ShouldBe("smart_toy");
        command.Metadata["agentIconColor"].ShouldBe("#FF4F4F");
    }

    [Fact]
    public async Task When_AgentHasNoIconKeyOrColor_Then_AgentPendingMetadataOmitsBoth()
    {
        // Given
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Pending, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 0, null, Now()));

        // Then
        command.ShouldNotBeNull();
        command.Metadata.ShouldNotContainKey("agentIconKey");
        command.Metadata.ShouldNotContainKey("agentIconColor");
    }

    [Fact]
    public async Task When_AgentPending_Then_AgentPendingMetadataIncludesAgentType()
    {
        // Given
        // When
        var command = await CaptureCommandAsync(new AgentUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Pending, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 0, null, Now()));

        // Then
        command.ShouldNotBeNull();
        command.Metadata["agentType"].ShouldBe("ci");
    }

    private async Task<BroadcastNotificationCommand?> CaptureCommandAsync(AgentUpsertedEvent @event)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>();

        var consumer = new OnAgentUpsertedBroadcast(readContext, publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
        return captured;
    }

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
