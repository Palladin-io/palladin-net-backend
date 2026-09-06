namespace Palladin.Module.Agents.Shared;

internal static class AgentPublicKey
{
    private const int PublicKeyBytes = 32;
    private const int SuffixLength = 8;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[PublicKeyBytes];
        if (!Convert.TryFromBase64String(value, bytes, out var written) || written != PublicKeyBytes)
        {
            return false;
        }

        normalized = Convert.ToBase64String(bytes);
        return string.Equals(value, normalized, StringComparison.Ordinal);
    }

    public static string Suffix(string publicKey) =>
        publicKey.Length <= SuffixLength ? publicKey : publicKey[^SuffixLength..];

    public static string Prefix(string publicKey) =>
        publicKey.Length <= SuffixLength ? publicKey : publicKey[..SuffixLength];

    public static string Hint(string publicKey) =>
        publicKey.Length <= SuffixLength * 2
            ? publicKey
            : $"{publicKey[..SuffixLength]}…{publicKey[^6..]}";
}
