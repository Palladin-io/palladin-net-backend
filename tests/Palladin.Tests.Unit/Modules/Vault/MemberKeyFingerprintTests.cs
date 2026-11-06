using Palladin.Core.Cryptography;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class MemberKeyFingerprintTests
{
    [Fact]
    public void When_FrozenSuiteKeyIsValid_Then_ComputesFrozenProtocolTwoFingerprint()
    {
        var rawPublicKey = Convert.FromHexString(
            "044E05AA8EEE253C1F1990665CAEEBB1F344DF5930CB4C8CD3DAA1F1D18BD212");

        var fingerprint = RecipientKeyFingerprint.Compute(
            LegacyWrapperSuite.Id, VaultKeyKind.MemberEncryption, rawPublicKey);

        fingerprint.ShouldBe(Convert.FromHexString(
            "90CB62C757F01E8B892589B775F0B24DEDBC5C01BB953DDA612D7CAEA3D92813"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(1215)]
    [InlineData(1217)]
    public void When_CurrentSuiteKeyHasWrongLength_Then_FailsClosed(int length) =>
        Should.Throw<DomainException>(() => RecipientKeyFingerprint.Compute(
            KeyWrapperSuites.Current.Id, VaultKeyKind.MemberEncryption, new byte[length]));

    [Fact]
    public void When_UnknownSuiteIsClaimed_Then_FailsClosed() =>
        Should.Throw<DomainException>(() => RecipientKeyFingerprint.Compute(
            "palladin-unknown-v1", VaultKeyKind.MemberEncryption, new byte[32]));
}
