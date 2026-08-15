using System.Text.Json;
using Palladin.Core.Json;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class VaultPreparedPayloadCodec
{
    private static readonly JsonSerializerOptions Options = new(
        PalladinJsonSerializationSettings.DefaultOptions
        ?? throw new InvalidOperationException("Shared Palladin JSON serialization is not configured."));

    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    internal static T Decode<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Options)
        ?? throw new JsonException("Prepared Vault payload is empty.");
}
