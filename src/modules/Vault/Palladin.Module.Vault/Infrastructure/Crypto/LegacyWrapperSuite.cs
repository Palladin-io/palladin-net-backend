using Palladin.Core.Cryptography;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

// Objects created before key-wrapper v1 carry no suite field (legacy manifests, v1 submissions and approver
// manifests, unsuffixed script packages); their absent suite means this read-only suite. It is never a write default.
internal static class LegacyWrapperSuite
{
    internal static KeyWrapperSuite Suite { get; } = KeyWrapperSuites.Resolve("palladin-x25519-sealed-box-v1");

    internal static string Id => Suite.Id;

    internal static int PackageBytes => Suite.PackageBytes;
}
