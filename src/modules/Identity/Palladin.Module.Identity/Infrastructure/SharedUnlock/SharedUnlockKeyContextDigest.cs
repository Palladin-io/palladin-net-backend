using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Shared;
using static Palladin.Module.Identity.Infrastructure.SharedUnlock.SharedUnlockEncoding;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal static class SharedUnlockKeyContextDigest
{
    internal const string Protocol = "palladin.shared-unlock.key-context.v1";

    internal static SharedUnlockKeyContext? FromUser(User user) =>
        user.IsOnboarded && user.KdfProfileId is not null && user.Salt is { Length: > 0 }
        && user.PublicKey is { Length: 32 } && user.EncryptedPrivateKey is { Length: > 0 }
        && user.MemberKeyVersion is > 0
            ? new SharedUnlockKeyContext(user.Id, user.SecurityVersion, user.MinimumSecurityVersion,
                user.KdfProfileId, Base64Url.EncodeToString(user.Salt), user.CredentialRevision,
                user.PrivateKeyWrapRevision, user.MemberKeyVersion.Value,
                Base64Url.EncodeToString(user.PublicKey), Base64Url.EncodeToString(user.EncryptedPrivateKey))
            : null;

    internal static byte[] Hash(SharedUnlockKeyContext context)
    {
        using var stream = new MemoryStream();
        Write(stream, Protocol);
        Write(stream, context.AccountId.ToString("D"));
        Write(stream, context.SecurityVersion.ToString(CultureInfo.InvariantCulture));
        Write(stream, context.MinimumSecurityVersion.ToString(CultureInfo.InvariantCulture));
        Write(stream, context.KdfProfileId);
        Write(stream, context.KdfSalt);
        Write(stream, context.CredentialRevision.ToString(CultureInfo.InvariantCulture));
        Write(stream, context.PrivateKeyWrapRevision.ToString(CultureInfo.InvariantCulture));
        Write(stream, context.MemberKeyVersion.ToString(CultureInfo.InvariantCulture));
        Write(stream, context.PublicKey);
        Write(stream, context.EncryptedPrivateKey);
        return SHA256.HashData(stream.ToArray());
    }
}
