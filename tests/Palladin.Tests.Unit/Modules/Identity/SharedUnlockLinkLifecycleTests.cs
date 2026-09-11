using NodaTime;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class SharedUnlockLinkLifecycleTests
{
    [Fact]
    public void When_LogoutFollowsDisconnect_Then_OnlyExplicitReconnectRemovesRevocation()
    {
        // Given
        var now = Instant.FromUnixTimeSeconds(1_800_000_000);
        var link = SharedUnlockLink.Create(Guid.NewGuid(), Guid.NewGuid(), now);
        link.TryDisconnect(1, 1, now).ShouldBeTrue();

        // When
        var loggedOut = link.TryLogout(2, 2, now);
        var implicitLock = link.TryLock(3, 3, now);
        var implicitActivate = link.TryActivateFromManualUnlock(3, 3, now);

        // Then
        loggedOut.ShouldBeTrue();
        link.State.ShouldBe(SharedUnlockLinkState.Revoked);
        link.Revision.ShouldBe(3u);
        link.Epoch.ShouldBe(3u);
        link.LastInvalidationSequence.ShouldBe(2u);
        link.LastLogoutSequence.ShouldBe(2u);
        implicitLock.ShouldBeFalse();
        implicitActivate.ShouldBeFalse();
        link.TryReconnect(3, 3, now).ShouldBeTrue();
        link.State.ShouldBe(SharedUnlockLinkState.Locked);
        link.LastLogoutSequence.ShouldBe(2u);
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 1u)]
    public void When_LogoutOfRevokedLinkIsStale_Then_AllClosingStateStaysUnchanged(uint revision, uint sequence)
    {
        // Given
        var now = Instant.FromUnixTimeSeconds(1_800_000_000);
        var link = SharedUnlockLink.Create(Guid.NewGuid(), Guid.NewGuid(), now);
        link.TryDisconnect(1, 1, now).ShouldBeTrue();

        // When
        var loggedOut = link.TryLogout(revision, sequence, now + Duration.FromSeconds(1));

        // Then
        loggedOut.ShouldBeFalse();
        link.State.ShouldBe(SharedUnlockLinkState.Revoked);
        link.Revision.ShouldBe(2u);
        link.Epoch.ShouldBe(2u);
        link.LastInvalidationSequence.ShouldBe(1u);
        link.LastLogoutSequence.ShouldBe(0u);
        link.UpdatedAt.ShouldBe(now);
    }
}
