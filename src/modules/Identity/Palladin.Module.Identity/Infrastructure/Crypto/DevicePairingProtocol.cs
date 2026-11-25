using Palladin.Core.Cryptography;

namespace Palladin.Module.Identity.Infrastructure.Crypto;

// identity-kdf v3 devicePairing protocolVersion 2 fixes the recipient KEM; a new KEM is a new pairing protocol version.
internal static class DevicePairingProtocol
{
    internal const int RecipientPublicKeyBytes = XWingKem.PublicKeyBytes;
    internal const int KemCiphertextBytes = XWingKem.CiphertextBytes;
}
