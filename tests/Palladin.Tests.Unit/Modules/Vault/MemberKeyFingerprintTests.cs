using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Infrastructure.Crypto;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class MemberKeyFingerprintTests
{
    [Fact]
    public void When_RawX25519KeyIsValid_Then_ComputesFrozenProtocolTwoFingerprint()
    {
        var rawPublicKey = Convert.FromHexString(
            "044E05AA8EEE253C1F1990665CAEEBB1F344DF5930CB4C8CD3DAA1F1D18BD212");

        var fingerprint = MemberKeyFingerprint.Compute(rawPublicKey);

        fingerprint.ShouldBe(Convert.FromHexString(
            "90CB62C757F01E8B892589B775F0B24DEDBC5C01BB953DDA612D7CAEA3D92813"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void When_RawX25519KeyHasWrongLength_Then_FailsClosed(int length)
    {
        var action = () => MemberKeyFingerprint.Compute(new byte[length]);

        action.ShouldThrow<DomainException>().Message.ShouldContain("exactly 32 raw RFC 7748 bytes");
    }
}
