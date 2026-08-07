namespace Palladin.Module.Agents.Shared;

internal static class AgentPublicKey
{
    private const int SuffixLength = 8;

    public static string Suffix(string publicKey) =>
        publicKey.Length <= SuffixLength ? publicKey : publicKey[^SuffixLength..];

    public static string Prefix(string publicKey) =>
        publicKey.Length <= SuffixLength ? publicKey : publicKey[..SuffixLength];
}
