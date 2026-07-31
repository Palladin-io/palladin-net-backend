namespace Palladin.Core.Ai;

public interface IAiClient
{
    Task<AiResult> ChatAsync(
        AiSettings settings,
        CancellationToken cancellationToken = default,
        params AiChatMessage[] messages);

    Task<AiResult<T>> ChatAsync<T>(
        AiSettings settings,
        CancellationToken cancellationToken = default,
        params AiChatMessage[] messages);
}

public record AiResult(string Response);
public record AiResult<T>(T Value, string Response) : AiResult(Response);

public sealed record AiChatMessage(string Role, string Content);

public sealed class AiUserRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

public sealed record AiPromptNavigation(string Key, params (string argName, string argValue)[] Args);

public sealed record AiSettings(
    string? Provider = null,
    string? Model = null,
    Guid? SessionId = null,
    AiPromptNavigation? PromptNavigation = null,
    string ConversationName = "AiClient");
