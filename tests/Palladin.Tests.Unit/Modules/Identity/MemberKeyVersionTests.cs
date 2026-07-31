using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class MemberKeyVersionTests
{
    [Fact]
    public void When_MemberPublicKeyRotates_Then_VersionIncrementsAndEventPublishesNewKey()
    {
        var initialKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var rotatedKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var now = Instant.FromUtc(2026, 7, 17, 9, 0);
        var user = CreatePasswordUser(initialKey, now);
        user.FetchEvents();

        user.RotateMemberKeyPair(
            rotatedKey,
            Enumerable.Repeat((byte)0x33, 32).ToArray(),
            Enumerable.Repeat((byte)0x44, 32).ToArray(),
            now + Duration.FromMinutes(1));

        user.MemberKeyVersion.ShouldBe((uint)2);
        user.PrivateKeyWrapRevision.ShouldBe(2u);
        user.PublicKey.ShouldBe(rotatedKey);
        var @event = user.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<UpsertMemberKeyDirectoryCommand>();
        @event.KeyVersion.ShouldBe((uint)2);
        @event.RawPublicKey.ShouldBe(rotatedKey);
    }

    [Fact]
    public void When_MemberPublicKeyRotationReusesCurrentKey_Then_FailsClosed()
    {
        var publicKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var now = Instant.FromUtc(2026, 7, 17, 9, 0);
        var user = CreatePasswordUser(publicKey, now);

        var rotate = () =>
            user.RotateMemberKeyPair(
                publicKey.ToArray(),
                new byte[32],
                new byte[32],
                now + Duration.FromMinutes(1));

        rotate.ShouldThrow<InvalidOperationException>();
        user.MemberKeyVersion.ShouldBe((uint)1);
    }

    [Fact]
    public void When_AccountRecoveryRewrapsPrivateKey_Then_PublicKeyAndVersionRemainUnchanged()
    {
        var publicKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var now = Instant.FromUtc(2026, 7, 17, 9, 0);
        var user = CreatePasswordUser(publicKey, now);
        user.FetchEvents();

        user.RecoverAccount(
            user.CredentialRevision,
            user.PrivateKeyWrapRevision,
            new byte[16],
            Enumerable.Repeat((byte)0x33, 32).ToArray(),
            new byte[16],
            Enumerable.Repeat((byte)0x44, 32).ToArray(),
            null,
            true,
            now + Duration.FromMinutes(1));

        user.PublicKey.ShouldBe(publicKey);
        user.MemberKeyVersion.ShouldBe((uint)1);
        user.FetchEvents().ShouldNotContain(@event => @event is UpsertMemberKeyDirectoryCommand);
    }

    [Fact]
    public void When_MemberKeyDirectoryIsRepublished_Then_CurrentKeyAndVersionArePreserved()
    {
        var publicKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var now = Instant.FromUtc(2026, 7, 17, 9, 0);
        var user = CreatePasswordUser(publicKey, now);
        user.FetchEvents();

        user.RepublishMemberKeyDirectory();

        user.MemberKeyVersion.ShouldBe((uint)1);
        user.PublicKey.ShouldBe(publicKey);
        var command = user.FetchEvents().ShouldHaveSingleItem()
            .ShouldBeOfType<UpsertMemberKeyDirectoryCommand>();
        command.KeyVersion.ShouldBe((uint)1);
        command.RawPublicKey.ShouldBe(publicKey);
    }

    private static User CreatePasswordUser(byte[] publicKey, Instant now) =>
        User.RegisterWithPassword(
            Guid.NewGuid(),
            "member@example.com",
            "Member",
            "en",
            Guid.NewGuid(),
            Permission.VaultManage,
            new byte[16],
            new byte[16],
            publicKey,
            new byte[32],
            new byte[32],
            null,
            "web",
            now);
}
