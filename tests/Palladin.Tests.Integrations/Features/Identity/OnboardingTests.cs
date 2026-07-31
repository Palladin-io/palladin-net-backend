using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Triggers;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

// Onboarding is materialized in Identity, fed by commands from the owning modules. Per-user steps
// (entryCreated, mobileRegistered) land on the User via MarkOnboardingStepCommand; org-level steps
// (apiKeyCreated, agentEnrolled) land on the Organization via MarkOrganizationOnboardingStepCommand.
[Collection<ApiFactoryCollection>]
public sealed class OnboardingTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_MarkOnboardingStepConsumed_Then_UserStateMaterialized()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();

        // When
        await apiFactory.ConsumeAsync<MarkOnboardingStepConsumer, MarkOnboardingStepCommand>(
            new MarkOnboardingStepCommand(user.Id, OnboardingStep.EntryCreated));

        // Then
        var stored = await QueryUserAsync(user.Id);
        stored.EntryCreated.ShouldBeTrue();
        stored.MobileRegistered.ShouldBeFalse();
    }

    [Fact]
    public async Task When_MarkOnboardingStepForUnknownUser_Then_NoOp()
    {
        // Given — no user seeded

        // When / Then — consuming does not throw
        await apiFactory.ConsumeAsync<MarkOnboardingStepConsumer, MarkOnboardingStepCommand>(
            new MarkOnboardingStepCommand(Guid.NewGuid(), OnboardingStep.EntryCreated));
    }

    [Fact]
    public async Task When_MarkOrganizationOnboardingStepConsumed_Then_OrgStateMaterialized()
    {
        // Given
        var (_, org, _) = await apiFactory.Services.SeedUserAsync();

        // When
        await apiFactory.ConsumeAsync<MarkOrganizationOnboardingStepConsumer, MarkOrganizationOnboardingStepCommand>(
            new MarkOrganizationOnboardingStepCommand(org.Id, OnboardingStep.AgentEnrolled));

        // Then
        var stored = await QueryOrgAsync(org.Id);
        stored.AgentEnrolled.ShouldBeTrue();
        stored.ApiKeyCreated.ShouldBeFalse();
    }

    [Fact]
    public async Task When_EntryCreated_Then_PublishesEntryCreatedStepForCreator()
    {
        // Given
        var (endpoint, get) = CaptureUser();
        var creator = Guid.NewGuid();

        // When
        await new OnEntryCreatedOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new EntryUpsertedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), creator,
                    EntityChange.Created, 1, apiFactory.FakeClock.GetCurrentInstant())));

        // Then
        var cmd = get().ShouldNotBeNull();
        cmd.UserId.ShouldBe(creator);
        cmd.Step.ShouldBe(OnboardingStep.EntryCreated);
    }

    [Fact]
    public async Task When_ApiKeyCreated_Then_PublishesOrgApiKeyStep()
    {
        // Given
        var (endpoint, get) = CaptureOrg();
        var orgId = Guid.NewGuid();

        // When
        await new OnApiKeyCreatedOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new ApiKeyCreatedEvent(Guid.NewGuid(), orgId, "CI", "ab12", Guid.NewGuid(), "Owner", Now())));

        // Then
        var cmd = get().ShouldNotBeNull();
        cmd.OrganizationId.ShouldBe(orgId);
        cmd.Step.ShouldBe(OnboardingStep.ApiKeyCreated);
    }

    [Fact]
    public async Task When_ActiveAgentUpserted_Then_PublishesOrgAgentEnrolledStep()
    {
        // Given
        var (endpoint, get) = CaptureOrg();
        var orgId = Guid.NewGuid();

        // When
        await new OnAgentEnrolledOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new AgentUpsertedEvent(Guid.NewGuid(), orgId, AgentStatus.Active, "pk", 1, "signing-pk", "Bot", "cli", null, null, 1, Now(), Now())));

        // Then
        var cmd = get().ShouldNotBeNull();
        cmd.OrganizationId.ShouldBe(orgId);
        cmd.Step.ShouldBe(OnboardingStep.AgentEnrolled);
    }

    [Fact]
    public async Task When_NonActiveAgentUpserted_Then_NoOrgStepPublished()
    {
        // Given
        var (endpoint, get) = CaptureOrg();

        // When
        await new OnAgentEnrolledOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new AgentUpsertedEvent(Guid.NewGuid(), Guid.NewGuid(), AgentStatus.Pending, "pk", 1, "signing-pk", "Bot", "cli", null, null, 0, null, Now())));

        // Then
        get().ShouldBeNull();
    }

    [Fact]
    public async Task When_MobilePushTokenRegistered_Then_PublishesMobileStep()
    {
        // Given
        var (endpoint, get) = CaptureUser();
        var userId = Guid.NewGuid();

        // When
        await new OnPushTokenRegisteredOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new PushTokenRegisteredEvent(Guid.NewGuid(), userId, Guid.NewGuid(), PushPlatform.Ios, Now())));

        // Then
        var cmd = get().ShouldNotBeNull();
        cmd.UserId.ShouldBe(userId);
        cmd.Step.ShouldBe(OnboardingStep.MobileRegistered);
    }

    [Fact]
    public async Task When_WebPushTokenRegistered_Then_NoStepPublished()
    {
        // Given
        var (endpoint, get) = CaptureUser();

        // When
        await new OnPushTokenRegisteredOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new PushTokenRegisteredEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Web, Now())));

        // Then
        get().ShouldBeNull();
    }

    [Fact]
    public async Task When_EntryUpdated_Then_NoOnboardingStepPublished()
    {
        // Given
        var (endpoint, get) = CaptureUser();

        // When — an update, not a creation
        await new OnEntryCreatedOnboarding(endpoint).Consume(
            apiFactory.MockConsumeContext(
                new EntryUpsertedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    EntityChange.Updated, 2, apiFactory.FakeClock.GetCurrentInstant())));

        // Then
        get().ShouldBeNull();
    }

    private static (IPublishEndpoint Endpoint, Func<MarkOnboardingStepCommand?> Get) CaptureUser()
    {
        MarkOnboardingStepCommand? captured = null;
        var endpoint = Substitute.For<IPublishEndpoint>();
        endpoint.When(p => p.Publish(Arg.Any<MarkOnboardingStepCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<MarkOnboardingStepCommand>());
        return (endpoint, () => captured);
    }

    private static (IPublishEndpoint Endpoint, Func<MarkOrganizationOnboardingStepCommand?> Get) CaptureOrg()
    {
        MarkOrganizationOnboardingStepCommand? captured = null;
        var endpoint = Substitute.For<IPublishEndpoint>();
        endpoint.When(p => p.Publish(Arg.Any<MarkOrganizationOnboardingStepCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<MarkOrganizationOnboardingStepCommand>());
        return (endpoint, () => captured);
    }

    private async Task<(bool EntryCreated, bool MobileRegistered)> QueryUserAsync(Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        return await ctx.Users
            .Where(u => u.Id == userId)
            .Select(u => new ValueTuple<bool, bool>(u.EntryCreated, u.MobileRegistered))
            .FirstAsync();
    }

    private async Task<(bool ApiKeyCreated, bool AgentEnrolled)> QueryOrgAsync(Guid organizationId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        return await ctx.Organizations
            .Where(o => o.Id == organizationId)
            .Select(o => new ValueTuple<bool, bool>(o.ApiKeyCreated, o.AgentEnrolled))
            .FirstAsync();
    }

    private Instant Now() => apiFactory.FakeClock.GetCurrentInstant();
}
