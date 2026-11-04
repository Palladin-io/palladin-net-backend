using Palladin.Core.Cryptography;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

// The signed vault-v2 Agent/approver manifests, script-execution/v1 and agent-submission/v1 freeze this suite and
// 32-byte recipient keys into their bytes. Those paths move to the registry's current suite only through new
// contract versions; Member Vault-key wrappers already follow the authenticated Member key directory.
internal static class FrozenContractWrapperSuite
{
    internal static KeyWrapperSuite Suite { get; } = KeyWrapperSuites.Resolve("palladin-x25519-sealed-box-v1");

    internal static string Id => Suite.Id;

    internal static int PackageBytes => Suite.PackageBytes;
}
