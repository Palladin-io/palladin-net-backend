using NodaTime;
using Palladin.Core.Cryptography;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class MemberKeyVersionTests
{
    [Fact]
    public void When_PasswordAccountRegisters_Then_DirectoryCommandCarriesCurrentSuiteKey()
    {
        // Given
        var publicKey = CurrentSuiteKey(0x11);

        // When
        var user = CreatePasswordUser(publicKey, Instant.FromUtc(2026, 7, 17, 9, 0));

        // Then
        user.MemberPublicKeySuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        var command = user.FetchEvents().OfType<UpsertMemberKeyDirectoryCommand>().ShouldHaveSingleItem();
        command.PublicKeySuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        command.RawPublicKey.ShouldBe(publicKey);
    }

    [Fact]
    public void When_RegistrationUsesReadOnlySuiteKeyLength_Then_FailsClosed() =>
        Should.Throw<InvalidOperationException>(() =>
            CreatePasswordUser(new byte[32], Instant.FromUtc(2026, 7, 17, 9, 0)));

    [Fact]
    public void When_AccountRecoveryRewrapsPrivateKey_Then_PublicKeyAndVersionRemainUnchanged()
    {
        var publicKey = CurrentSuiteKey(0x11);
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
            "web",
            now + Duration.FromMinutes(1));

        user.PublicKey.ShouldBe(publicKey);
        user.MemberKeyVersion.ShouldBe((uint)1);
        user.FetchEvents().ShouldNotContain(@event => @event is UpsertMemberKeyDirectoryCommand);
    }

    [Fact]
    public void When_MemberKeyDirectoryIsRepublished_Then_CurrentKeyAndVersionArePreserved()
    {
        var publicKey = CurrentSuiteKey(0x11);
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

    private static byte[] CurrentSuiteKey(byte fill) =>
        Enumerable.Repeat(fill, KeyWrapperSuites.Current.PublicKeyBytes).ToArray();

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
            KeyWrapperSuites.Current.Id,
            publicKey,
            new byte[32],
            new byte[32],
            null,
            "web",
            now);
}
