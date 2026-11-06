using Palladin.Core.Cryptography;

namespace Palladin.Module.Identity.Infrastructure.Recovery;

internal static class RecoveryChallenges
{
    internal static (ushort Version, byte[] Package) Seal(Guid userId, ReadOnlySpan<byte> secret, byte[] memberPublicKey,
        string memberPublicKeySuiteId) =>
        memberPublicKeySuiteId == KeyWrapperSuites.Current.Id
            ? (XWingRecoveryChallengeSealer.Version, XWingRecoveryChallengeSealer.Seal(userId, secret, memberPublicKey))
            : (RecoveryChallengeSealer.Version, RecoveryChallengeSealer.Seal(userId, secret, memberPublicKey));
}
