using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palladin.Core.Json;

public sealed class Base64UrlByteArrayJsonConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A base64url string is required.");
        }

        var encoded = reader.GetString()!;
        try
        {
            var padding = (encoded.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new JsonException("The base64url value has an invalid length."),
            };
            var value = Convert.FromBase64String(
                encoded.Replace('-', '+').Replace('_', '/') + padding);
            if (Encode(value) != encoded)
            {
                throw new JsonException("The base64url value is not canonical.");
            }

            return value;
        }
        catch (FormatException exception)
        {
            throw new JsonException("The base64url value is invalid.", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Encode(value));

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
