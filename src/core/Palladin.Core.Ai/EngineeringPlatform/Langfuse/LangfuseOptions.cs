namespace Palladin.Core.Ai.EngineeringPlatform.Langfuse;

internal class LangfuseOptions
{
    public const string Position = "Langfuse";

    public string ApiUrl { get; init; } = "https://cloud.langfuse.com/";
    public string OTelUrl { get; init; } = "https://cloud.langfuse.com/api/public/otel/v1/traces";

    public string Pk { get; init; } = null!;
    public string Sk { get; init; } = null!;

    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(10);
}
