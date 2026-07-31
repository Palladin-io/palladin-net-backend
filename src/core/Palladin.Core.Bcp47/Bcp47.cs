namespace Palladin.Core.Bcp47;

public sealed record Bcp47(string Language, string Region)
{
    public override string ToString() => $"{Language}-{Region}";

    public static Bcp47 From(string value)
    {
        var tags = value.Split('-');
        if (tags.Length != 2)
        {
            throw new ArgumentException("Invalid BCP47 format");
        }

        return new Bcp47(tags[0], tags[1]);
    }

    public static implicit operator Bcp47(string str) => From(str);
    public static implicit operator string(Bcp47 bcp47) => bcp47.ToString();
}
