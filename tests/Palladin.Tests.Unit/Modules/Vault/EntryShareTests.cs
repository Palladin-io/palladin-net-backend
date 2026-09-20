using NodaTime;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Domain;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntryShareTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 20, 12, 0);

    [Fact]
    public void When_RetryingAnAlreadyDeliveredSession_Then_TheLimitIsNotConsumedTwice()
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);

        // When
        share.Deliver(session, Now).ShouldBeTrue();
        var deliveredVersion = share.MutationVersion;
        share.Deliver(session, Now + Duration.FromSeconds(1)).ShouldBeFalse();

        // Then
        share.DeliveryCount.ShouldBe(1);
        share.FirstDeliveredAt.ShouldBe(Now);
        share.LastDeliveredAt.ShouldBe(Now);
        share.MutationVersion.ShouldBeGreaterThan(deliveredVersion);
        share.Activities.Count(x => x.Kind == EntryShareActivityKind.Delivered).ShouldBe(1);
    }

    [Fact]
    public void When_OneRecipientConsumesTheLastReceipt_Then_OtherSessionsCannotReceive()
    {
        // Given
        var share = CreateShare();
        var first = OpenSession(share);
        var second = OpenSession(share);
        share.Deliver(first, Now);

        // When
        Action action = () => share.Deliver(second, Now);

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.DeliveryCount.ShouldBe(1);
        second.DeliveredAt.ShouldBeNull();
    }

    [Fact]
    public void When_TheClientNeverConfirms_Then_TheReceiptStaysConsumed()
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);

        // When
        share.Deliver(session, Now);

        // Then
        share.DeliveryCount.ShouldBe(share.MaximumReceipts);
        share.FirstConfirmedAt.ShouldBeNull();
        share.Activities.ShouldNotContain(x => x.NotifySender);
        Should.Throw<EntryShareUnavailableException>(() => OpenSession(share));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void When_ReceiptIsConfirmed_Then_OnlyTheFirstConfirmationMayNotify(bool notifications)
    {
        // Given
        var share = CreateShare(maximumReceipts: 2, notifications: notifications);
        var first = OpenSession(share);
        var second = OpenSession(share);
        share.Deliver(first, Now);
        share.Deliver(second, Now);

        // When
        share.ConfirmReceipt(first, Now).ShouldBeTrue();
        share.ConfirmReceipt(first, Now).ShouldBeFalse();
        share.ConfirmReceipt(second, Now + Duration.FromSeconds(1)).ShouldBeTrue();

        // Then
        share.FirstConfirmedAt.ShouldBe(Now);
        share.Activities.Count(x => x.Kind == EntryShareActivityKind.Confirmed).ShouldBe(2);
        share.Activities.Count(x => x.NotifySender).ShouldBe(notifications ? 1 : 0);
        share.DeliveryCount.ShouldBe(2);
        share.FetchEvents().OfType<EntryShareActivityEvent>()
            .Count(x => x.Kind == EntryShareActivityKind.Confirmed).ShouldBe(2);
    }

    [Fact]
    public void When_ConfirmationArrivesBeforeDelivery_Then_ItCannotTriggerTheNotification()
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);

        // When
        Action action = () => share.ConfirmReceipt(session, Now);

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.FirstConfirmedAt.ShouldBeNull();
        share.Activities.ShouldNotContain(x => x.NotifySender);
    }

    [Fact]
    public void When_NamedRecipientHasNotVerifiedEmail_Then_NoCiphertextIsDelivered()
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient);
        var session = OpenSession(share);

        // When
        Action action = () => share.Deliver(session, Now);

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.DeliveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(EntryShareProtection.Password)]
    [InlineData(EntryShareProtection.Pin)]
    public void When_OnlyOneOfTwoGatesIsVerified_Then_DeliveryIsDenied(EntryShareProtection protection)
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient, protection: protection);
        var emailOnly = OpenSession(share);
        share.IssueOtp(emailOnly, new byte[32], Now, Duration.FromMinutes(3), Duration.FromSeconds(30), new(1, "protected-code", "en"));
        share.VerifyEmail(emailOnly, Now);
        var secretOnly = OpenSession(share);
        share.VerifySecret(secretOnly, Now);

        // When
        Action emailOnlyAction = () => share.Deliver(emailOnly, Now);
        Action secretOnlyAction = () => share.Deliver(secretOnly, Now);

        // Then
        emailOnlyAction.ShouldThrow<EntryShareUnavailableException>();
        secretOnlyAction.ShouldThrow<EntryShareUnavailableException>();
        share.DeliveryCount.ShouldBe(0);
        share.VerifySecret(emailOnly, Now);
        share.Deliver(emailOnly, Now).ShouldBeTrue();
    }

    [Fact]
    public void When_TheCodeIsVerified_Then_ItCannotBeReplayed()
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient);
        var session = OpenSession(share);
        share.IssueOtp(session, new byte[32], Now, Duration.FromMinutes(3), Duration.FromSeconds(30), new(1, "protected-code", "en"));

        // When
        share.VerifyEmail(session, Now);

        // Then
        session.OtpHash.ShouldBeNull();
        session.OtpExpiresAt.ShouldBeNull();
        Should.Throw<EntryShareUnavailableException>(() => share.VerifyEmail(session, Now));
    }

    [Fact]
    public void When_TheCodeExpires_Then_VerificationIsDeniedWithoutConsumingAReceipt()
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient);
        var session = OpenSession(share);
        share.IssueOtp(session, new byte[32], Now, Duration.FromMinutes(3), Duration.FromSeconds(30), new(1, "protected-code", "en"));

        // When
        var action = () => share.VerifyEmail(session, Now + Duration.FromMinutes(3));

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public void When_ANewSessionRequestsOtpDuringCooldown_Then_ResendingIsStillDenied()
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient);
        var first = OpenSession(share);
        var second = OpenSession(share);
        share.IssueOtp(first, new byte[32], Now, Duration.FromMinutes(3), Duration.FromSeconds(30), new(1, "protected-code", "en"));

        // When
        var action = () => share.IssueOtp(second, new byte[32], Now + Duration.FromSeconds(29),
            Duration.FromMinutes(3), Duration.FromSeconds(30), new(1, "protected-code", "en"));

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        second.OtpHash.ShouldBeNull();
    }

    [Theory]
    [InlineData(EntryShareProtection.None)]
    [InlineData(EntryShareProtection.Password)]
    [InlineData(EntryShareProtection.Pin)]
    public void When_ProtectionChanges_Then_PreviouslyVerifiedSessionsAreInvalid(EntryShareProtection next)
    {
        // Given
        var share = CreateShare(protection: EntryShareProtection.Password);
        var session = OpenSession(share);
        share.VerifySecret(session, Now);

        // When
        share.ChangeProtection(next, next == EntryShareProtection.None ? null : "new-verifier", Now);

        // Then
        Should.Throw<EntryShareUnavailableException>(() => share.Deliver(session, Now));
        Should.Throw<EntryShareUnavailableException>(() => share.EndByRecipient(session, Now));
        share.DeliveryCount.ShouldBe(0);
        share.SecurityVersion.ShouldBe(2);
    }

    [Fact]
    public void When_TooManyVerificationAttemptsFail_Then_AllSessionsAreTemporarilyBlocked()
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);

        // When
        share.RegisterFailedAttempt(Now, 2, 10, Duration.FromMinutes(1));
        share.RegisterFailedAttempt(Now, 2, 10, Duration.FromMinutes(1));

        // Then
        Should.Throw<EntryShareUnavailableException>(() => share.Deliver(session, Now));
        Should.Throw<EntryShareUnavailableException>(() => OpenSession(share));
        share.DeliveryCount.ShouldBe(0);
        share.Deliver(session, Now + Duration.FromMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void When_TotalAttemptBudgetIsExhausted_Then_WaitingOutCooldownCannotRestartGuessing()
    {
        // Given
        var share = CreateShare();
        share.RegisterFailedAttempt(Now, 2, 4, Duration.FromMinutes(1));
        share.RegisterFailedAttempt(Now, 2, 4, Duration.FromMinutes(1));
        var afterCooldown = Now + Duration.FromMinutes(1);

        // When
        share.RegisterFailedAttempt(afterCooldown, 2, 4, Duration.FromMinutes(1));
        share.RegisterFailedAttempt(afterCooldown, 2, 4, Duration.FromMinutes(1));

        // Then
        share.FailedAttempts.ShouldBe(4);
        share.LockedUntil.ShouldBe(share.ExpiresAt);
        Should.Throw<EntryShareUnavailableException>(() =>
            share.OpenSession(Guid.NewGuid(), new byte[32], afterCooldown + Duration.FromMinutes(2),
                Duration.FromMinutes(10)));
    }

    [Fact]
    public void When_LinkExpires_Then_EvenAnExactDeliveryRetryIsDenied()
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);
        share.Deliver(session, Now);

        // When
        Action action = () => share.Deliver(session, share.ExpiresAt);

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.Expire(share.ExpiresAt).ShouldBeTrue();
        share.Expire(share.ExpiresAt).ShouldBeFalse();
        share.Ciphertext.ShouldBeEmpty();
        share.SecretVerifier.ShouldBeNull();
        share.ProtectedRecipientEmail.ShouldBeNull();
        share.Activities.Count(x => x.Kind == EntryShareActivityKind.Expired).ShouldBe(1);
    }

    [Theory]
    [InlineData(EntryShareActivityKind.RevokedBySender)]
    [InlineData(EntryShareActivityKind.SourceAccessRemoved)]
    public void When_AuthorityRevokesTheLink_Then_ItErasesThePacketAndRejectsRetries(EntryShareActivityKind reason)
    {
        // Given
        var share = CreateShare();
        var session = OpenSession(share);
        share.Deliver(session, Now);

        // When
        share.Revoke(reason, Now).ShouldBeTrue();
        share.Revoke(reason, Now).ShouldBeFalse();

        // Then
        Should.Throw<EntryShareUnavailableException>(() => share.Deliver(session, Now));
        share.Ciphertext.ShouldBeEmpty();
        share.AccessTokenHash.ShouldBeEmpty();
        share.RevocationReason.ShouldBe(reason);
        share.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public void When_AnAuthorizedRecipientEndsTheLink_Then_OtherRecipientsLoseAccess()
    {
        // Given
        var share = CreateShare(maximumReceipts: 2);
        var first = OpenSession(share);
        var second = OpenSession(share);

        // When
        share.EndByRecipient(first, Now);

        // Then
        Should.Throw<EntryShareUnavailableException>(() => share.Deliver(second, Now));
        share.RevocationReason.ShouldBe(EntryShareActivityKind.EndedByRecipient);
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public void When_AnUnverifiedRecipientTriesToEndTheLink_Then_TheLinkRemainsActive()
    {
        // Given
        var share = CreateShare(recipientMode: EntryShareRecipientMode.NamedRecipient);
        var session = OpenSession(share);

        // When
        Action action = () => share.EndByRecipient(session, Now);

        // Then
        action.ShouldThrow<EntryShareUnavailableException>();
        share.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void When_ASessionBelongsToAnotherLink_Then_ItCannotDeliverConfirmOrEnd()
    {
        // Given
        var share = CreateShare();
        var foreign = OpenSession(CreateShare());

        // When
        Action deliver = () => share.Deliver(foreign, Now);
        Action confirm = () => share.ConfirmReceipt(foreign, Now);
        Action end = () => share.EndByRecipient(foreign, Now);

        // Then
        deliver.ShouldThrow<EntryShareUnavailableException>();
        confirm.ShouldThrow<EntryShareUnavailableException>();
        end.ShouldThrow<EntryShareUnavailableException>();
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public void When_SessionLifetimeExceedsLinkLifetime_Then_TheSessionIsCapped()
    {
        // Given
        var share = CreateShare();

        // When
        var session = share.OpenSession(Guid.NewGuid(), new byte[32], Now, Duration.FromDays(1));

        // Then
        session.ExpiresAt.ShouldBe(share.ExpiresAt);
    }

    [Fact]
    public void When_AnInvalidSnapshotOrMissingExpiryIsSubmitted_Then_NoShareIsCreated()
    {
        // Given
        var source = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        // When
        var action = () => EntryShare.Create(Guid.NewGuid(), source, new EntryRevision(1), Guid.NewGuid(),
            Now, Now, 1, EntryShareRecipientMode.AnyoneWithLink, null, EntryShareProtection.None, null,
            new byte[32], new byte[24], new byte[16], false, 1, Now);

        // Then
        action.ShouldThrow<DomainException>();
    }

    private static EntryShareSession OpenSession(EntryShare share) =>
        share.OpenSession(Guid.NewGuid(), new byte[32], Now, Duration.FromMinutes(10));

    private static EntryShare CreateShare(
        int maximumReceipts = 1,
        bool notifications = true,
        EntryShareRecipientMode recipientMode = EntryShareRecipientMode.AnyoneWithLink,
        EntryShareProtection protection = EntryShareProtection.None) => EntryShare.Create(
        Guid.NewGuid(), new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new EntryRevision(1),
        Guid.NewGuid(), Now, Now + Duration.FromHours(1), maximumReceipts, recipientMode,
        recipientMode == EntryShareRecipientMode.NamedRecipient ? "protected-address" : null,
        protection, protection == EntryShareProtection.None ? null : "secret-verifier",
        new byte[32], new byte[24], new byte[16], notifications, 1, Now);
}
