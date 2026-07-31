namespace Palladin.Module.Notification.Infrastructure.Push;

internal sealed class PushTextOptions
{
    public const string Position = "Modules:Notification:PushText";

    public const string DefaultLanguage = "en";

    public Dictionary<string, PushTextEntry> Default { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, PushTextEntry>> ByType { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PushTextEntry
{
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
}
