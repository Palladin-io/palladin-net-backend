using NodaTime;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class SessionSecondFactorTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 10, 12, 0);

    [Fact]
    public void When_FactorIsReplacedAtSameInstant_Then_OldSessionCannotSatisfyIt()
    {
        // Given
        var factor = TotpCredential.StartEnrollment(Guid.NewGuid(), "synthetic-factor", Now);
        factor.Confirm(1, Now);
        var token = Create(factor.UserId, factor.ConfigurationRevision, Now);
        token.SatisfiesSecondFactor(factor, Now).ShouldBeTrue();

        // When
        factor.Disable(Now);
        factor.RestartEnrollment("replacement-factor", Now);
        factor.Confirm(2, Now);

        // Then
        token.SatisfiesSecondFactor(factor, Now).ShouldBeFalse();
        factor.ConfigurationRevision.ShouldBe(5u);
    }

    [Fact]
    public void When_OtherLoginUsesCode_Then_ExistingAssuranceRemainsValid()
    {
        // Given
        var factor = TotpCredential.StartEnrollment(Guid.NewGuid(), "synthetic-factor", Now);
        factor.Confirm(1, Now);
        var token = Create(factor.UserId, factor.ConfigurationRevision, Now);

        // When
        factor.RecordUsedTimeStep(2, Now + Duration.FromMinutes(1));

        // Then
        token.SatisfiesSecondFactor(factor, Now + Duration.FromMinutes(1)).ShouldBeTrue();
        token.SecondFactorVerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void When_SessionHasNoAssurance_Then_EnabledFactorRequiresVerification()
    {
        // Given
        var factor = TotpCredential.StartEnrollment(Guid.NewGuid(), "synthetic-factor", Now);
        factor.Confirm(1, Now);
        var token = Create(factor.UserId);

        // When
        var accepted = token.SatisfiesSecondFactor(factor, Now);

        // Then
        accepted.ShouldBeFalse();
        token.SatisfiesSecondFactor(null, Now).ShouldBeTrue();
        factor.Disable(Now);
        token.SatisfiesSecondFactor(factor, Now).ShouldBeTrue();
    }

    [Fact]
    public void When_ForeignFactorOrExpiredOrRevokedSession_Then_AssuranceIsRejected()
    {
        // Given
        var factor = TotpCredential.StartEnrollment(Guid.NewGuid(), "synthetic-factor", Now);
        factor.Confirm(1, Now);
        var token = Create(factor.UserId, factor.ConfigurationRevision, Now);
        var foreign = Create(Guid.NewGuid(), factor.ConfigurationRevision, Now);

        // When
        var expired = token.SatisfiesSecondFactor(factor, Now + Duration.FromHours(1));
        token.Revoke(Now);

        // Then
        expired.ShouldBeFalse();
        foreign.SatisfiesSecondFactor(factor, Now).ShouldBeFalse();
        token.SatisfiesSecondFactor(factor, Now).ShouldBeFalse();
        token.SatisfiesSecondFactor(null, Now).ShouldBeFalse();
    }

    [Fact]
    public void When_AssuranceIsIncompleteOrFuture_Then_SessionCreationFails()
    {
        // Given
        var userId = Guid.NewGuid();

        // When
        Action missingTime = () => Create(userId, 1);
        Action missingRevision = () => Create(userId, null, Now);
        Action zeroRevision = () => Create(userId, 0, Now);
        Action future = () => Create(userId, 1, Now + Duration.FromSeconds(1));

        // Then
        missingTime.ShouldThrow<ArgumentException>();
        missingRevision.ShouldThrow<ArgumentException>();
        zeroRevision.ShouldThrow<ArgumentException>();
        future.ShouldThrow<ArgumentException>();
    }

    private static RefreshToken Create(Guid userId, uint? revision = null, Instant? verifiedAt = null) =>
        RefreshToken.Create(Guid.NewGuid(), userId, Guid.NewGuid(), "synthetic-hash", 1,
            Now + Duration.FromHours(1), Now, revision, verifiedAt);
}
