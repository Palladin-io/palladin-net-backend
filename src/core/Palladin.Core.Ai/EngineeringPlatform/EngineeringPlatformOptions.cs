using Palladin.Core.Ai.EngineeringPlatform.Langfuse;

namespace Palladin.Core.Ai.EngineeringPlatform;

internal sealed class EngineeringPlatformOptions
{
    public const string Position = "EngineeringPlatform";

    public EngineeringPlatformProvider Provider { get; init; } = EngineeringPlatformProvider.Langfuse;

    public LangfuseOptions? LangfuseOptions { get; init; }
}

internal enum EngineeringPlatformProvider
{
    Langfuse = 0,
    Langsmith = 1
}
