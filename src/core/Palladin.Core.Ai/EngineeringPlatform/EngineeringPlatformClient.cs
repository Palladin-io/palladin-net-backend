using System.Text.Json;

namespace Palladin.Core.Ai.EngineeringPlatform;

internal interface IEngineeringPlatformClient
{
   Task<Prompt> GetPromptAsync(string promptName, CancellationToken cancellationToken = default);
}

internal sealed record Prompt(
    EngineeringPlatformProvider Provider,
    string Name,
    string Version,
    JsonElement? Config,
    bool IsActive,
    ICollection<string> Labels,
    ICollection<string> Tags,
    ICollection<AiChatMessage> Messages);
