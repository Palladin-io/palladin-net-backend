using NodaTime;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class LoginLockoutTests
{
    [Fact]
    public void When_ThresholdIsReached_Then_LockoutEventIsRaisedOncePerWindow()
    {
        var now = Instant.FromUtc(2026, 8, 22, 12, 0);
        var lockout = LoginLockout.Create(
            Guid.NewGuid(),
            "member@example.com",
            "198.51.100.10",
            now);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            lockout.RecordFailure(5, Duration.FromMinutes(15), Duration.FromMinutes(15), now);
        }

        lockout.RecordFailure(5, Duration.FromMinutes(15), Duration.FromMinutes(15), now);
        var @event = lockout.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<LoginLockedOutEvent>();

        @event.EmailHash.ShouldNotContain("member@example.com");
        @event.LockedUntil.ShouldBe(now + Duration.FromMinutes(15));
        lockout.FetchEvents().ShouldBeEmpty();
    }
}
