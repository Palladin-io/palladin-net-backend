using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
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
public sealed class ApprovedBroadcastMetadataTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentApprovedBroadcast_Then_MetadataIncludesAgentType()
    {
        // Given
        var @event = new AgentUpsertedEvent(
            Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Active, "pk", 1, "signing-pk", "deploy-bot", "ci", null, null, 1, Now(), Now());

        // When
        var command = await CaptureAgentResolvedAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.AgentApproved);
        command.Metadata["agentType"].ShouldBe("ci");
    }

    [Fact]
    public async Task When_AgentApprovedBroadcast_Then_MetadataIncludesApproverActorName()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var approver = await apiFactory.Services.SeedAgentsUserAsync(Guid.NewGuid(), displayName: "Ada Lovelace");
        var agent = await apiFactory.Services.SeedAgentAsync(
            organizationId,
            AgentFaker.Create(organizationId: organizationId)
                .RuleFor(x => x.EnrolledBy, approver.Id));

        var @event = new AgentUpsertedEvent(
            agent.Id, organizationId, AgentStatus.Active, agent.PublicKey, 1, agent.SigningPublicKey, agent.Name, agent.Type, null, null, 1, Now(), Now());

        // When
        var command = await CaptureAgentResolvedAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.AgentApproved);
        command.Metadata["actorName"].ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task When_GrantApprovedBroadcast_Then_MetadataIncludesMethods()
    {
        // Given
        var @event = new GrantApprovedEvent(
            GrantId: Guid.NewGuid(),
            VaultId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            AgentId: Guid.NewGuid(),
            EntryId: Guid.NewGuid(),
            ApprovedBy: Guid.NewGuid(),
            Type: GrantType.Granular,
            AgentName: "deploy-bot",
            EntryLabel: "GitHub token",
            VaultName: "Prod",
            ActorName: "Alice",
            ExpirySource: "default",
            ExpiresAt: null,
            QueryLimit: null,
            Methods: GrantMethods.Get | GrantMethods.Inject,
            UpdatedAt: Now());

        // When
        var command = await CaptureGrantApprovedAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.GrantApproved);
        command.Metadata["methods"].ShouldBe((GrantMethods.Get | GrantMethods.Inject).ToString());
        command.Metadata.Keys.ShouldNotContain(key =>
            new[] { "agentName", "entryLabel", "vaultName", "actorName", "reason" }.Contains(key));
        command.Metadata.Values.ShouldNotContain("GitHub token");
        command.Metadata.Values.ShouldNotContain("Prod");
    }

    [Fact]
    public async Task When_GrantDeniedBroadcast_Then_MetadataIncludesMethods()
    {
        // Given
        var @event = new GrantDeniedEvent(
            GrantId: Guid.NewGuid(),
            VaultId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            AgentId: Guid.NewGuid(),
            DeniedBy: Guid.NewGuid(),
            EntryId: Guid.NewGuid(),
            AgentName: "deploy-bot",
            EntryLabel: "GitHub token",
            VaultName: "Prod",
            ActorName: "Alice",
            Methods: GrantMethods.Get | GrantMethods.Inject,
            UpdatedAt: Now());

        // When
        var command = await CaptureGrantDeniedAsync(@event);

        // Then
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.GrantDenied);
        command.Metadata["methods"].ShouldBe((GrantMethods.Get | GrantMethods.Inject).ToString());
        command.Metadata.Keys.ShouldNotContain(key =>
            new[] { "agentName", "entryLabel", "vaultName", "actorName", "reason" }.Contains(key));
    }

    private async Task<BroadcastNotificationCommand?> CaptureAgentResolvedAsync(AgentUpsertedEvent @event)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new OnAgentResolvedBroadcast(
            scope.ServiceProvider.GetRequiredService<AgentsDomainReadContext>(), publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
        return captured;
    }

    private async Task<BroadcastNotificationCommand?> CaptureGrantApprovedAsync(GrantApprovedEvent @event)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        var consumer = new OnGrantApprovedBroadcast(publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
        return captured;
    }

    private async Task<BroadcastNotificationCommand?> CaptureGrantDeniedAsync(GrantDeniedEvent @event)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        var consumer = new OnGrantDeniedBroadcast(publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(@event));
        return captured;
    }

    private static Instant Now() =>
        Instant.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
