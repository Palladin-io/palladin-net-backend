using System.Text.RegularExpressions;

namespace Palladin.Module.Identity.Domain;

public readonly partial record struct PreferredLanguage
{
    public const string DefaultCode = "en";

    public static readonly PreferredLanguage Default = new(DefaultCode);

    public string Code { get; }

    private PreferredLanguage(string code) => Code = code;

    public static PreferredLanguage From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var code = value.Trim().ToLowerInvariant().Split('-')[0];

        return Iso6391Pattern().IsMatch(code) ? new PreferredLanguage(code) : Default;
    }

    public override string ToString() => Code;

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex Iso6391Pattern();
}
