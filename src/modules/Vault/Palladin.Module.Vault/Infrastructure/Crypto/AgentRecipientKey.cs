using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

// The Agent replica's encryption key publication is the only authority for Agent-addressed wrappers.
internal static class AgentRecipientKey
{
    internal static byte[] Fingerprint(Agent agent) => Fingerprint(agent.PublicKeySuiteId, agent.PublicKey);

    internal static byte[] Fingerprint(string suiteId, string base64PublicKey) =>
        RecipientKeyFingerprint.Compute(suiteId, VaultKeyKind.AgentEncryption, Convert.FromBase64String(base64PublicKey));
}
