using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Palladin.Module.Identity.Domain;
using static Palladin.Module.Identity.Infrastructure.SharedUnlock.SharedUnlockEncoding;

namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal static class SharedUnlockTranscript
{
    internal const string Protocol = "palladin.shared-unlock.v1";
    internal const string Suite = "X25519-HKDF-SHA256-XCHACHA20POLY1305";
    internal static byte[] Challenge() => RandomNumberGenerator.GetBytes(32);
    internal static string Direction(SharedUnlockDirection direction) => direction switch
    {
        SharedUnlockDirection.WebToExtension => "web-to-extension",
        SharedUnlockDirection.ExtensionToWeb => "extension-to-web",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    internal static byte[] Hash(SharedUnlockOperation operation) => SHA256.HashData(Encode(operation));
    internal static byte[] Encode(SharedUnlockOperation operation)
    {
        using var stream = new MemoryStream();
        Write(stream, Protocol);
        Write(stream, Suite);
        Write(stream, Protocol);
        Write(stream, Direction(operation.Direction));
        Write(stream, operation.Id.ToString("D"));
        Write(stream, operation.UserId.ToString("D"));
        Write(stream, operation.OrganizationId.ToString("D"));
        Write(stream, operation.ApiOrigin);
        Write(stream, operation.WebOrigin);
        Write(stream, operation.ExtensionId);
        Write(stream, operation.DocumentBinding);
        Write(stream, Base64Url.EncodeToString(operation.WebGeneration));
        Write(stream, Base64Url.EncodeToString(operation.ExtensionGeneration));
        Write(stream, operation.LinkId.ToString("D"));
        Write(stream, operation.LinkEpoch.ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.PreferenceRevision.ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.AuthorizationVersion.ToString(CultureInfo.InvariantCulture));
        Write(stream, Base64Url.EncodeToString(operation.KeyContextDigest));
        Write(stream, operation.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.UnlockedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.IdleDeadline.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.AbsoluteDeadline.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, operation.OfflineDeadline.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        Write(stream, Base64Url.EncodeToString(operation.SourcePublicKey));
        Write(stream, Base64Url.EncodeToString(operation.RecipientPublicKey));
        return stream.ToArray();
    }

    internal static SharedUnlockProofAuthority Proof(SharedUnlockOperation operation) =>
        new(operation.Id, operation.Challenge, operation.TranscriptHash,
            operation.RecipientProofPublicKey, operation.IssuedAt, operation.ExpiresAt);
}
