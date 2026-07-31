using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palladin.Core.Ietf;

public sealed class IetfLangJsonConverter : JsonConverter<IetfLang>
{
    public override IetfLang Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return IetfLang.From(s ?? string.Empty);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
                    return IetfLang.From(valueProp.GetString() ?? string.Empty);

                if (root.TryGetProperty("language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
                    return IetfLang.From(langProp.GetString() ?? string.Empty);

                if (root.TryGetProperty("Language", out var langProp2) && langProp2.ValueKind == JsonValueKind.String)
                    return IetfLang.From(langProp2.GetString() ?? string.Empty);
            }
        }

        throw new JsonException($"Cannot deserialize {nameof(IetfLang)} from token {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, IetfLang value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Language);
    }
}
