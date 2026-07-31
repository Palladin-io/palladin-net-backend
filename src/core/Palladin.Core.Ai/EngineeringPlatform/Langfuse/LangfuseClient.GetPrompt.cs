using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Palladin.Core.Ai.Exceptions;
using Microsoft.Extensions.Caching.Memory;

namespace Palladin.Core.Ai.EngineeringPlatform.Langfuse;

internal sealed partial class LangfuseClient
{
    public async Task<Prompt> GetPromptAsync(string promptName, CancellationToken cancellationToken = default)
    {
        var cached = await cache.GetOrCreateAsync(promptName, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = langfuseOptions.Value.CacheDuration;

            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/public/v2/prompts/{promptName}");
            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var langfusePrompt = await response.Content.ReadFromJsonAsync<LangfusePrompt>(cancellationToken) ??
                                 throw new PromptNotFoundException(promptName);

            return new Prompt(
                EngineeringPlatformProvider.Langfuse,
                promptName,
                langfusePrompt.Version.ToString(),
                langfusePrompt.Config,
                langfusePrompt.IsActive ?? false,
                langfusePrompt.Labels,
                langfusePrompt.Tags,
                GetMessages(langfusePrompt)
            );
        });

        return cached!;
    }

    private ICollection<AiChatMessage> GetMessages(LangfusePrompt langfusePrompt)
    {
        return langfusePrompt switch
        {
            TextLangfusePrompt textLangfusePrompt => [new AiChatMessage(AiUserRoles.System, TransformBrackets(textLangfusePrompt.Prompt))],
            ChatLangfusePrompt chatLangfusePrompt => chatLangfusePrompt.Prompt
                .Select(x => new AiChatMessage(x.Role, TransformBrackets(x.Content)))
                .ToList(),
            _ => throw new ArgumentException($"Unknown prompt type: {langfusePrompt.Type}"),
        };
    }

    public static string TransformBrackets(string input) => input.Replace("{{", "{").Replace("}}", "}");
}

[JsonConverter(typeof(PromptBaseConverter))]
internal abstract record LangfusePrompt
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    [JsonPropertyName("labels")]
    public ICollection<string> Labels { get; init; } = new List<string>();

    [JsonPropertyName("tags")]
    public ICollection<string> Tags { get; init; } = new List<string>();

    [JsonPropertyName("commitMessage")]
    public string? CommitMessage { get; init; }

    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    [JsonPropertyName("projectId")]
    public string ProjectId { get; init; } = null!;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; init; } = null!;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = null!;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; init; } = null!;

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; init; }
}

internal sealed record ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = null!;

    [JsonPropertyName("content")]
    public string Content { get; init; } = null!;
}

internal sealed record ChatLangfusePrompt : LangfusePrompt
{
    [JsonPropertyName("prompt")]
    public ICollection<ChatMessage> Prompt { get; init; } = new List<ChatMessage>();

    public override string Type => "chat";
}

internal sealed record TextLangfusePrompt : LangfusePrompt
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = null!;

    public override string Type => "text";
}

internal sealed class PromptBaseConverter : JsonConverter<LangfusePrompt>
{
    public override LangfusePrompt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var discriminator = root.GetProperty("type").GetString()!;

        LangfusePrompt? result = discriminator.ToLowerInvariant() switch
        {
            "chat" => JsonSerializer.Deserialize<ChatLangfusePrompt>(root.GetRawText(), options),
            "text" => JsonSerializer.Deserialize<TextLangfusePrompt>(root.GetRawText(), options),
            _ => throw new JsonException($"Unknown prompt type: {discriminator}")
        };

        return result ?? throw new JsonException("Deserialization resulted in null.");
    }

    public override void Write(Utf8JsonWriter writer, LangfusePrompt value, JsonSerializerOptions options)
    {
        if (value is ChatLangfusePrompt chatPrompt)
        {
            JsonSerializer.Serialize(writer, chatPrompt, options);
        }
        else if (value is TextLangfusePrompt textPrompt)
        {
            JsonSerializer.Serialize(writer, textPrompt, options);
        }
        else
        {
            throw new JsonException("Unknown type when serializing PromptBase.");
        }
    }
}
