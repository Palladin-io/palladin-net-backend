namespace Palladin.Core.Analytics;

public sealed class PostHogOptions
{
    public const string Position = "PostHog";

    public string ProjectApiKey { get; set; } = string.Empty;

    public string Host { get; set; } = "https://eu.i.posthog.com";
}
