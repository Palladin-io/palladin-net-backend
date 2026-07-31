using System.Text.Json.Serialization;

namespace Palladin.Core.Ietf;

[JsonConverter(typeof(IetfLangJsonConverter))]
public sealed record IetfLang(string Language)
{
    public override string ToString() => Language;

    public static IetfLang From(string value)
    {
        return new IetfLang(value);
    }

    public static implicit operator IetfLang(string str) => From(str);
    public static implicit operator string(IetfLang lang) => lang.ToString();
}
