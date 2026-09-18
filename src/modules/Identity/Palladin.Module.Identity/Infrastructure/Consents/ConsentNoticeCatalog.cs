using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Consents;

internal sealed class ConsentNoticeCatalog
{
    private readonly IReadOnlyList<ConsentNotice> _versions;

    public ConsentNoticeCatalog()
    {
        using var stream = typeof(ConsentNoticeCatalog).Assembly.GetManifestResourceStream(
            "Palladin.Module.Identity.Infrastructure.Consents.notice-versions.json")!;
        _versions = JsonSerializer.Deserialize<ConsentNotice[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    internal ConsentNoticeCatalog(IReadOnlyList<ConsentNotice> versions) => _versions = versions;

    internal ConsentNotice? Find(string purpose, string locale, string version, Instant now)
    {
        var effectiveFrom = InstantPattern.ExtendedIso.Parse(version);
        if (!effectiveFrom.Success || effectiveFrom.Value > now)
        {
            return null;
        }

        return _versions.SingleOrDefault(notice => notice.Purpose == purpose
            && notice.Locale == locale && notice.Version == version);
    }
}

internal sealed class ConsentOptions
{
    public const string Position = "Modules:Identity:Consents";
    public int MaxAgeSeconds { get; init; } = 60;
}
