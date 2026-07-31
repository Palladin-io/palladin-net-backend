using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class DeactivatedBroadcastTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentDeactivated_Then_BroadcastEmittedWithAgentDeactivatedType()
    {
        // Given
        var @event = new AgentDeactivatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Operator", "Agent", 1, Now());

        // When
        var command = await CaptureCommandAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.AgentDeactivated);
        command.Category.ShouldBe(NotificationCategory.Update);
        command.CollapsesPending.ShouldBeTrue();
    }

    [Fact]
    public async Task When_AgentDeactivated_Then_MetadataIncludesAgentIdAndActionLink()
    {
        // Given
        var agentId = Guid.NewGuid();
        var @event = new AgentDeactivatedEvent(agentId, Guid.NewGuid(), Guid.NewGuid(), "Operator", "Agent", 1, Now());

        // When
        var command = await CaptureCommandAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Metadata["agentId"].ShouldBe(agentId.ToString());
        command.Metadata["actionType"].ShouldBe("view_agent");
        command.Metadata["actionDeepLink"].ShouldBe($"/agents/{agentId}");
    }

    [Fact]
    public async Task When_AgentDeactivatedByUser_Then_MetadataIncludesActorName()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var deactivator = await apiFactory.Services.SeedAgentsUserAsync(Guid.NewGuid(), displayName: "Bob Deactivator");
        var agent = await apiFactory.Services.SeedAgentAsync(
            organizationId,
            AgentFaker.Create(organizationId: organizationId)
                .RuleFor(x => x.DeactivatedBy, deactivator.Id)
                .RuleFor(x => x.Status, AgentStatus.Deactivated));

        var @event = new AgentDeactivatedEvent(agent.Id, organizationId, deactivator.Id, "Operator", "Agent", 1, Now());

        // When
        var command = await CaptureCommandAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Metadata["actorName"].ShouldBe("Bob Deactivator");
    }

    [Fact]
    public async Task When_AgentHasIconKeyAndColor_Then_DeactivatedMetadataIncludesBoth()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organizationId,
            AgentFaker.Create(organizationId: organizationId)
                .RuleFor(x => x.IconKey, "smart_toy")
                .RuleFor(x => x.IconColor, "#FF4F4F")
                .RuleFor(x => x.Status, AgentStatus.Deactivated));

        var @event = new AgentDeactivatedEvent(agent.Id, organizationId, Guid.NewGuid(), "Operator", "Agent", 1, Now());

        // When
        var command = await CaptureCommandAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Metadata["agentIconKey"].ShouldBe("smart_toy");
        command.Metadata["agentIconColor"].ShouldBe("#FF4F4F");
    }

    private async Task<BroadcastNotificationCommand?> CaptureCommandAsync(AgentDeactivatedEvent @event)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnAgentDeactivated(
            scope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>(), publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
        return captured;
    }

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
