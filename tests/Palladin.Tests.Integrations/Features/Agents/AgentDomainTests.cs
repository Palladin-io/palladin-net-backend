using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Contracts.Events;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

public sealed class AgentDomainTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 2, 12, 0);

    [Fact]
    public void Create_EmitsAgentUpserted()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now, "Claude");

        var events = agent.FetchEvents();
        var replicated = events.ShouldHaveSingleItem().ShouldBeOfType<AgentUpsertedEvent>();
        replicated.Status.ShouldBe(AgentStatus.Pending);
        replicated.PublicKey.ShouldBe("pubkey");
        replicated.AccessEpoch.ShouldBe(0u);
    }

    [Fact]
    public void Activate_EmitsAgentUpsertedWithActiveStatus()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now);
        agent.FetchEvents();

        agent.Activate(Guid.NewGuid(), Now, "Claude", "assistant", "icon", "#fff");

        var replicated = agent.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<AgentUpsertedEvent>();
        replicated.Status.ShouldBe(AgentStatus.Active);
        replicated.AccessEpoch.ShouldBe(1u);
        replicated.AccessEpochStartedAt.ShouldBe(Now);
    }

    [Fact]
    public void Deactivate_EmitsUpsertedAndDeactivatedEvents()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now);
        agent.Activate(Guid.NewGuid(), Now, null, null, null, null);
        agent.FetchEvents();

        var deactivatedBy = Guid.NewGuid();
        agent.RequestDeactivation(Guid.NewGuid(), deactivatedBy, Now);
        agent.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<AgentDeactivationRequestedEvent>();
        agent.CompleteDeactivation("Operator", Now);

        var events = agent.FetchEvents();
        var replicated = events.OfType<AgentUpsertedEvent>().ShouldHaveSingleItem();
        replicated.AccessEpoch.ShouldBe(1u);
        replicated.AccessEpochStartedAt.ShouldBeNull();
        var deactivated = events.OfType<AgentDeactivatedEvent>().ShouldHaveSingleItem();
        deactivated.DeactivatedBy.ShouldBe(deactivatedBy);
        deactivated.AccessEpoch.ShouldBe(1u);
    }

    [Fact]
    public void DeactivatePending_EmitsNeverActivatedEpoch()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now);
        agent.FetchEvents();

        agent.RequestDeactivation(Guid.NewGuid(), Guid.NewGuid(), Now);
        agent.FetchEvents();
        agent.CompleteDeactivation("Operator", Now);

        var events = agent.FetchEvents();
        var replicated = events.OfType<AgentUpsertedEvent>().ShouldHaveSingleItem();
        replicated.Status.ShouldBe(AgentStatus.Deactivated);
        replicated.AccessEpoch.ShouldBe(0u);
        replicated.AccessEpochStartedAt.ShouldBeNull();
        events.OfType<AgentDeactivatedEvent>().ShouldHaveSingleItem().AccessEpoch.ShouldBe(0u);
    }

    [Fact]
    public void Reactivate_EmitsUpsertedAndReactivatedEvents()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now);
        agent.Activate(Guid.NewGuid(), Now, null, null, null, null);
        agent.RequestDeactivation(Guid.NewGuid(), Guid.NewGuid(), Now);
        agent.FetchEvents();
        agent.CompleteDeactivation("Operator", Now);
        agent.FetchEvents();

        var reactivatedBy = Guid.NewGuid();
        var reactivatedAt = Now + Duration.FromMinutes(1);
        agent.Reactivate(reactivatedBy, "Operator", reactivatedAt);

        var events = agent.FetchEvents();
        var replicated = events.OfType<AgentUpsertedEvent>().ShouldHaveSingleItem();
        replicated.Status.ShouldBe(AgentStatus.Active);
        replicated.AccessEpoch.ShouldBe(2u);
        replicated.AccessEpochStartedAt.ShouldBe(reactivatedAt);
        var reactivated = events.OfType<AgentReactivatedEvent>().ShouldHaveSingleItem();
        reactivated.ReactivatedBy.ShouldBe(reactivatedBy);
    }

    [Fact]
    public void UpdateActiveMetadata_PreservesSourceAccessEpoch()
    {
        var agent = Agent.Create(Guid.NewGuid(), Guid.NewGuid(), "pubkey", "signpub", "assistant", Now);
        agent.Activate(Guid.NewGuid(), Now, null, null, null, null);
        agent.FetchEvents();

        agent.Update(Now + Duration.FromMinutes(1), "Renamed", null, null, null, null);

        var replicated = agent.FetchEvents().ShouldHaveSingleItem().ShouldBeOfType<AgentUpsertedEvent>();
        replicated.UpdatedAt.ShouldBe(Now + Duration.FromMinutes(1));
        replicated.AccessEpoch.ShouldBe(1u);
        replicated.AccessEpochStartedAt.ShouldBe(Now);
    }
}
