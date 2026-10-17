using NodaTime;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntrySharingAuthorityTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 20, 12, 0);

    [Fact]
    public void When_ARevocationIsReplayedOrArrivesOutOfOrder_Then_OldAuthorizationNeverReturns()
    {
        // Given
        var authority = EntryShareSenderAuthority.Create(Guid.NewGuid(), Guid.NewGuid());
        authority.RevokeThrough(5);
        var version = authority.MutationVersion;

        // When
        authority.RevokeThrough(3);
        authority.RevokeThrough(5);

        // Then
        authority.RevokedThroughAuthorizationVersion.ShouldBe(5u);
        authority.MutationVersion.ShouldBe(version);
        authority.Allows(0).ShouldBeFalse();
        authority.Allows(4).ShouldBeFalse();
        authority.Allows(5).ShouldBeFalse();
        authority.Allows(6).ShouldBeTrue();
    }

    [Fact]
    public void When_AnOrganizationIsDisabledTwice_Then_TheRevocationIsIdempotent()
    {
        // Given
        var organization = VaultOrganizationLifecycle.Create(Guid.NewGuid());
        organization.DisableSharing();
        var version = organization.MutationVersion;

        // When
        organization.DisableSharing();
        organization.FenceMutation();

        // Then
        organization.SharingDisabled.ShouldBeTrue();
        organization.MutationVersion.ShouldBe(version + 1);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("vault")]
    [InlineData("entry")]
    [InlineData("member")]
    [InlineData("share")]
    [InlineData("expiry")]
    public void When_AReservationBindingIsSubstituted_Then_ItCannotBeConsumed(string changed)
    {
        // Given
        var scope = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var member = Guid.NewGuid();
        var share = Guid.NewGuid();
        var expires = Now + Duration.FromMinutes(5);
        var reservation = EntryShareCreationChallenge.Create(scope, member, share, expires);
        var requestedScope = new EntryScope(
            changed == "organization" ? Guid.NewGuid() : scope.OrganizationId,
            changed == "vault" ? Guid.NewGuid() : scope.VaultId,
            changed == "entry" ? Guid.NewGuid() : scope.EntryId);

        // When
        Action validate = () => reservation.Validate(changed == "share" ? Guid.NewGuid() : share,
            requestedScope, changed == "member" ? Guid.NewGuid() : member, changed == "expiry" ? expires : Now);

        // Then
        validate.ShouldThrow<EntryShareUnavailableException>();
    }

    [Fact]
    public void When_AnExpiredReservationIsRenewed_Then_OnlyTheNewShareCanBeConsumed()
    {
        // Given
        var scope = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var member = Guid.NewGuid();
        var oldShare = Guid.NewGuid();
        var newShare = Guid.NewGuid();
        var reservation = EntryShareCreationChallenge.Create(scope, member, oldShare, Now);

        // When
        reservation.Renew(newShare, Now, Duration.FromMinutes(5));

        // Then
        reservation.MutationVersion.ShouldBe(2);
        reservation.ExpiresAt.ShouldBe(Now + Duration.FromMinutes(5));
        Should.Throw<EntryShareUnavailableException>(() => reservation.Validate(oldShare, scope, member, Now));
        Should.NotThrow(() => reservation.Validate(newShare, scope, member, Now));
    }

    [Fact]
    public void When_AnActiveReservationIsRenewed_Then_TheOriginalBindingRemainsIntact()
    {
        // Given
        var scope = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var member = Guid.NewGuid();
        var share = Guid.NewGuid();
        var reservation = EntryShareCreationChallenge.Create(scope, member, share, Now + Duration.FromMinutes(5));

        // When
        Action renew = () => reservation.Renew(Guid.NewGuid(), Now, Duration.FromMinutes(5));

        // Then
        renew.ShouldThrow<DomainException>();
        reservation.MutationVersion.ShouldBe(1);
        reservation.ShareId.ShouldBe(share);
    }
}
