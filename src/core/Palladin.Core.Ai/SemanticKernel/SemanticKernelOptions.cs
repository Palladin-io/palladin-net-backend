using JetBrains.Annotations;

namespace Palladin.Core.Ai.SemanticKernel;

internal sealed class SemanticKernelOptions
{
    public static string Position = "SemanticKernel";

    public string DefaultProvider { get; set; } = null!;

    [UsedImplicitly]
    public IDictionary<string, LlmOptions> GenericProviders { get; init; } = new Dictionary<string, LlmOptions>();
}

[PublicAPI]
internal sealed class LlmOptions
{
    public string Key { get; set; } = null!;
    public string DefaultModelId { get; set; } = "gpt-4o-2024-08-06";
    public string Url { get; set; } = null!;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}
