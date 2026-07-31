using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using NodaTime;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class AgentAccessEpochTests
{
    [Fact]
    public void When_ActiveMetadataChanges_Then_AccessEpochDoesNotMove()
    {
        // Given
        var activatedAt = Instant.FromUtc(2026, 7, 17, 10, 0);
        var agent = Create(AgentStatus.Active, activatedAt);
        var metadataUpdatedAt = activatedAt + Duration.FromMinutes(1);

        // When
        agent.Apply(
            AgentStatus.Active,
            "public-key",
            1,
            "signing-key",
            "Renamed",
            null,
            null,
            1,
            activatedAt,
            metadataUpdatedAt);

        // Then
        agent.UpdatedAt.ShouldBe(metadataUpdatedAt);
        agent.AccessEpoch.ShouldBe(1u);
        agent.AccessEpochStartedAt.ShouldBe(activatedAt);
    }

    [Fact]
    public void When_AgentReactivates_Then_StartsNewAccessEpoch()
    {
        // Given
        var deactivatedAt = Instant.FromUtc(2026, 7, 17, 10, 0);
        var agent = Create(AgentStatus.Deactivated, deactivatedAt);
        var reactivatedAt = deactivatedAt + Duration.FromMinutes(1);

        // When
        agent.Apply(
            AgentStatus.Active,
            "public-key",
            1,
            "signing-key",
            "Agent",
            null,
            null,
            2,
            reactivatedAt,
            reactivatedAt);

        // Then
        agent.AccessEpoch.ShouldBe(2u);
        agent.AccessEpochStartedAt.ShouldBe(reactivatedAt);
    }

    [Fact]
    public void When_PendingAgentIsDeactivated_Then_AcceptsEpochZeroExactlyOnce()
    {
        // Given
        var createdAt = Instant.FromUtc(2026, 7, 17, 10, 0);
        var deactivatedAt = createdAt + Duration.FromMinutes(1);
        var agent = Create(AgentStatus.Pending, createdAt);

        // When
        var firstDeliveryAccepted = agent.AcceptDeactivation(0, deactivatedAt);
        var redeliveryAccepted = agent.AcceptDeactivation(0, deactivatedAt);

        // Then
        firstDeliveryAccepted.ShouldBeTrue();
        redeliveryAccepted.ShouldBeFalse();
        agent.Status.ShouldBe(AgentStatus.Deactivated);
        agent.AccessEpoch.ShouldBe(0u);
        agent.LastProcessedDeactivationEpoch.ShouldBe(0u);
        agent.LastProcessedDeactivationAt.ShouldBe(deactivatedAt);
    }

    private static Agent Create(AgentStatus status, Instant updatedAt) =>
        Agent.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            status,
            "public-key",
            1,
            "signing-key",
            "Agent",
            null,
            null,
            status == AgentStatus.Pending ? 0u : 1u,
            status == AgentStatus.Active ? updatedAt : null,
            updatedAt);
}
