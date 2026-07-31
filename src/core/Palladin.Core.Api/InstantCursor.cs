using System.Text;
using NodaTime;
using NodaTime.Text;

namespace Palladin.Core.Api;

public sealed record InstantCursor(Instant Timestamp, Guid Id)
{
    public static string Encode(Instant timestamp, Guid id)
    {
        var raw = $"{InstantPattern.ExtendedIso.Format(timestamp)}|{id}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static InstantCursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|');
            if (parts.Length != 2)
            {
                return null;
            }

            var instantParse = InstantPattern.ExtendedIso.Parse(parts[0]);
            if (!instantParse.Success || !Guid.TryParse(parts[1], out var id))
            {
                return null;
            }

            return new InstantCursor(instantParse.Value, id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
