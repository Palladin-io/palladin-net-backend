using JetBrains.Annotations;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public sealed record SharedUnlockKeyContext(Guid AccountId, ushort SecurityVersion,
    ushort MinimumSecurityVersion, string KdfProfileId, string KdfSalt,
    uint CredentialRevision, uint PrivateKeyWrapRevision, uint MemberKeyVersion,
    string PublicKey, string EncryptedPrivateKey);
