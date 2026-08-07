using System.Text.RegularExpressions;
using Palladin.Core.Types;
using Palladin.Module.Notification.Shared;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Notification.Infrastructure.Push;

[UsedImplicitly]
internal sealed partial class PushText(IOptions<PushTextOptions> options)
{
    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    private readonly PushTextOptions _options = options.Value;

    public (string Title, string Body) For(NotificationType type, string? language)
    {
        var (title, body) = Resolve(type, language);
        // Push is rendered while the client may be locked. Never interpolate publisher metadata;
        // configured placeholders are stripped so names or account details cannot reach a lock screen.
        return (Fill(title, NoMetadata), Fill(body, NoMetadata));
    }

    private (string Title, string Body) Resolve(NotificationType type, string? language)
    {
        var lang = Normalize(language);
        var wire = type.ToWire();

        if (_options.ByType.TryGetValue(wire, out var byLanguage) && TryResolve(byLanguage, lang, out var typed))
        {
            return typed;
        }

        if (TryResolve(_options.Default, lang, out var fallback))
        {
            return fallback;
        }

        return (string.Empty, string.Empty);
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> metadata)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{'))
        {
            return template;
        }

        var filled = PlaceholderRegex().Replace(
            template,
            match => metadata.TryGetValue(match.Groups[1].Value, out var value) ? value : string.Empty);

        var normalized = WhitespaceRegex().Replace(filled, " ").Trim();
        return PunctuationWhitespaceRegex().Replace(normalized, "$1");
    }

    [GeneratedRegex(@"\{([a-zA-Z]+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+([.,!?;:])")]
    private static partial Regex PunctuationWhitespaceRegex();

    private static bool TryResolve(
        IReadOnlyDictionary<string, PushTextEntry> byLanguage,
        string language,
        out (string Title, string Body) text)
    {
        if (byLanguage.TryGetValue(language, out var entry)
            || byLanguage.TryGetValue(PushTextOptions.DefaultLanguage, out entry))
        {
            text = (entry.Title, entry.Body);
            return true;
        }

        text = default;
        return false;
    }

    private static string Normalize(string? language) =>
        string.IsNullOrWhiteSpace(language) ? PushTextOptions.DefaultLanguage : language;
}
