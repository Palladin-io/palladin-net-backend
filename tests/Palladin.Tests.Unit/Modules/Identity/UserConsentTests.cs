using NodaTime;
using Palladin.Module.Identity.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class UserConsentTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 11, 12, 0);
    private static ConsentNotice Notice(string purpose = ConsentPurpose.ProductAnalytics) =>
        new(purpose, ConsentPurpose.Scope(purpose), "2026-09-10T00:00:00Z", "en");

    [Fact]
    public void When_NoDecisionExists_Then_ConsentIsUnknown()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);

        // Then
        consent.Status.ShouldBe("unknown");
        consent.Revision.ShouldBe(0u);
    }

    [Fact]
    public void When_GrantedThenRevoked_Then_WithdrawalPreservesNoticeAndServerTime()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);
        consent.TryDecide(true, 0, Guid.NewGuid(), Notice(), "web_onboarding", Now);
        var requestId = Guid.NewGuid();

        // When
        var history = consent.TryDecide(false, 1, requestId, Notice(), "mobile_settings", Now + Duration.FromMinutes(1));

        // Then
        consent.Status.ShouldBe("withdrawn");
        consent.Revision.ShouldBe(2u);
        history.ShouldNotBeNull();
        history.Status.ShouldBe("withdrawn");
        history.RecordedAt.ShouldBe(Now + Duration.FromMinutes(1));
        history.NoticeVersion.ShouldBe(Notice().Version);
        history.NoticeText.ShouldBeEmpty();
        history.Source.ShouldBe("mobile_settings");
        history.RequestId.ShouldBe(requestId);
        history.UserId.ShouldBe(consent.UserId);
    }

    [Fact]
    public void When_GrantUsesRevisionBeforeWithdrawal_Then_ItCannotRestoreConsent()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);
        consent.TryDecide(true, 0, Guid.NewGuid(), Notice(), "web_settings", Now);
        consent.TryDecide(false, 1, Guid.NewGuid(), Notice(), "mobile_settings", Now);

        // When
        var rejected = consent.TryDecide(true, 1, Guid.NewGuid(), Notice(), "web_settings", Now);

        // Then
        rejected.ShouldBeNull();
        consent.Status.ShouldBe("withdrawn");
        consent.Revision.ShouldBe(2u);
    }

    [Fact]
    public void When_AlreadyDeniedUserChoosesOffAgain_Then_ConcurrentOldGrantIsFenced()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);
        consent.TryDecide(false, 0, Guid.NewGuid(), Notice(), "web_settings", Now);

        // When
        consent.TryDecide(false, 1, Guid.NewGuid(), Notice(), "mobile_settings", Now);
        var oldGrant = consent.TryDecide(true, 1, Guid.NewGuid(), Notice(), "web_settings", Now);

        // Then
        oldGrant.ShouldBeNull();
        consent.Status.ShouldBe("denied");
        consent.Revision.ShouldBe(2u);
    }

    [Fact]
    public void When_ConsentNoticeHasAnotherPurpose_Then_DecisionIsNotRecorded()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);

        // When
        Should.Throw<ArgumentException>(() => consent.TryDecide(true, 0, Guid.NewGuid(),
            Notice(ConsentPurpose.EmailMarketing), "web_settings", Now));

        // Then
        consent.Revision.ShouldBe(0u);
        consent.Status.ShouldBe("unknown");
    }

    [Fact]
    public void When_ConsentIsGrantedOnAnotherDevice_Then_ActivationStaysValidUntilWithdrawal()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);
        consent.TryDecide(true, 0, Guid.NewGuid(), Notice(), "web_settings", Now);
        var firstActivation = consent.ActivationRevision;

        // When
        consent.TryDecide(true, 1, Guid.NewGuid(), Notice(), "mobile_settings", Now);

        // Then
        consent.ActivationRevision.ShouldBe(firstActivation);
        consent.TryDecide(false, 2, Guid.NewGuid(), Notice(), "web_settings", Now);
        consent.ActivationRevision.ShouldBe(0u);
        consent.TryDecide(true, 3, Guid.NewGuid(), Notice(), "mobile_settings", Now);
        consent.ActivationRevision.ShouldBeGreaterThan(firstActivation);
    }

    [Fact]
    public void When_ReplayChangesPayload_Then_HistoryDoesNotMatch()
    {
        // Given
        var consent = UserConsent.Create(Guid.NewGuid(), ConsentPurpose.ProductAnalytics);
        var history = consent.TryDecide(true, 0, Guid.NewGuid(), Notice(), "web_settings", Now)!;

        // Then
        history.Matches(true, 0, "2026-09-10T00:00:00Z", "en", "web_settings").ShouldBeTrue();
        history.Matches(false, 0, "2026-09-10T00:00:00Z", "en", "web_settings").ShouldBeFalse();
        history.Matches(true, 1, "2026-09-10T00:00:00Z", "en", "web_settings").ShouldBeFalse();
        history.Matches(true, 0, "test-v2", "en", "web_settings").ShouldBeFalse();
        history.Matches(true, 0, "2026-09-10T00:00:00Z", "pl", "web_settings").ShouldBeFalse();
        history.Matches(true, 0, "2026-09-10T00:00:00Z", "en", "mobile_settings").ShouldBeFalse();
    }
}
