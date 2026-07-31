using NodaTime;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class AgentVaultDiscoveryEnvelopeTests
{
    [Fact]
    public void When_ExactManifestRetryArrives_Then_ItIsIdempotent()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 10, 0));

        // When
        var changed = envelope.Replace(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 11, 0));

        // Then
        changed.ShouldBeFalse();
        envelope.ProvisionedAt.ShouldBe(Instant.FromUtc(2026, 7, 17, 10, 0));
    }

    [Fact]
    public void When_ManifestRevisionIsReusedWithDifferentCiphertext_Then_FailsClosed()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            SystemClock.Instance.GetCurrentInstant());
        var replay = provisioning with { AgentWrappedVdk = Enumerable.Repeat((byte)99, 120).ToArray() };

        // When
        Action action = () => envelope.Replace(replay, Guid.NewGuid(), 1, SystemClock.Instance.GetCurrentInstant());

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cannot be reused");
    }

    [Fact]
    public void When_ManifestRevisionMovesBackwards_Then_FailsClosed()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            SystemClock.Instance.GetCurrentInstant());
        var previousManifest = provisioning.Manifest with { ManifestRevision = new ManifestRevision(6) };
        var previous = provisioning with
        {
            ManifestRevision = new ManifestRevision(6),
            Manifest = previousManifest,
        };

        // When
        Action action = () => envelope.Replace(
            previous,
            Guid.NewGuid(),
            1,
            SystemClock.Instance.GetCurrentInstant());

        // Then
        action.ShouldThrow<DomainException>().Message.ShouldContain("cannot move backwards");
    }

    [Fact]
    public void When_RevokedManifestIsRetriedExactly_Then_ItRemainsRevoked()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 10, 0));
        var revokedAt = Instant.FromUtc(2026, 7, 17, 11, 0);
        envelope.RevokeForAcceptedAgentDeactivation(revokedAt, 1).ShouldBeTrue();

        // When
        var changed = envelope.Replace(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 12, 0));

        // Then
        changed.ShouldBeFalse();
        envelope.RevokedAt.ShouldBe(revokedAt);
    }

    [Fact]
    public void When_RevokedManifestIsReplacedByHigherRevision_Then_ItBecomesCurrent()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 10, 0));
        envelope.RevokeForAcceptedAgentDeactivation(Instant.FromUtc(2026, 7, 17, 11, 0), 1);
        var nextSignature = Enumerable.Repeat((byte)42, 64).ToArray();
        var nextManifest = provisioning.Manifest with
        {
            ManifestRevision = new ManifestRevision(8),
            Signature = nextSignature,
        };
        var next = provisioning with
        {
            ManifestRevision = new ManifestRevision(8),
            ManifestSignature = nextSignature,
            AgentWrappedVdk = Enumerable.Repeat((byte)24, 120).ToArray(),
            Manifest = nextManifest,
        };

        // When
        var changed = envelope.Replace(
            next,
            Guid.NewGuid(),
            2,
            Instant.FromUtc(2026, 7, 17, 12, 0));

        // Then
        changed.ShouldBeTrue();
        envelope.ManifestRevision.ShouldBe(new ManifestRevision(8));
        envelope.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void When_AcceptedDeactivationPredatesRacyProvisioning_Then_ItStillRevokesCurrentManifest()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 10, 0));
        var deactivatedAt = Instant.FromUtc(2026, 7, 17, 9, 59);

        // When
        var changed = envelope.RevokeForAcceptedAgentDeactivation(deactivatedAt, 1);

        // Then
        changed.ShouldBeTrue();
        envelope.RevokedAt.ShouldBe(deactivatedAt);
        envelope.ManifestRevision.ShouldBe(new ManifestRevision(7));
    }

    [Fact]
    public void When_ManifestWasProvisionedInCurrentAccessEpoch_Then_DelayedDeactivationDoesNotRevokeIt()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            2,
            Instant.FromUtc(2026, 7, 17, 11, 0));
        var deactivatedAt = Instant.FromUtc(2026, 7, 17, 9, 0);

        // When
        var changed = envelope.RevokeForAcceptedAgentDeactivation(
            deactivatedAt,
            1);

        // Then
        changed.ShouldBeFalse();
        envelope.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void When_PreviousEpochManifestSharesReactivationTimestamp_Then_DelayedDeactivationRevokesIt()
    {
        // Given
        var provisioning = CreateProvisioning(7);
        var sharedTimestamp = Instant.FromUtc(2026, 7, 17, 10, 0);
        var envelope = AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            sharedTimestamp);

        // When
        var changed = envelope.RevokeForAcceptedAgentDeactivation(
            Instant.FromUtc(2026, 7, 17, 9, 0),
            1);

        // Then
        changed.ShouldBeTrue();
        envelope.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public void When_FrozenAgentVdkPackageIsSealed_Then_CanonicalBoundAcceptsIt()
    {
        // Given
        var provisioning = CreateProvisioning(7) with { AgentWrappedVdk = new byte[120] };

        // When
        var action = () => AgentVaultDiscoveryEnvelope.Create(
            provisioning,
            Guid.NewGuid(),
            1,
            Instant.FromUtc(2026, 7, 17, 10, 0));

        // Then
        action.ShouldNotThrow();
    }

    private static ValidatedAgentDiscoveryProvisioning CreateProvisioning(ulong revision)
    {
        var scope = new VaultScope(Guid.NewGuid(), Guid.NewGuid());
        var agentId = Guid.NewGuid();
        var signature = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
        var fingerprint = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var manifest = new ValidatedVaultManifest(
            2,
            1,
            scope,
            agentId,
            fingerprint,
            fingerprint,
            fingerprint,
            fingerprint,
            new ManifestSigningKeyVersion(1),
            fingerprint,
            fingerprint,
            new AgentMessageKeyVersion(1),
            new VdkVersion(1),
            fingerprint,
            new ManifestRevision(revision),
            Instant.FromUtc(2026, 7, 17, 9, 0),
            2,
            signature);
        return new ValidatedAgentDiscoveryProvisioning(
            scope,
            agentId,
            2,
            1,
            new VdkVersion(1),
            new AgentRecipientKeyVersion(1),
            fingerprint,
            Enumerable.Range(0, 120).Select(x => (byte)x).ToArray(),
            new ManifestRevision(revision),
            signature,
            manifest);
    }
}
