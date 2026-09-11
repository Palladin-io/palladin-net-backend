using System.Text.Json;
using Palladin.Module.Identity.Domain;

namespace Palladin.Module.Identity.Infrastructure.Consents;

internal sealed class ConsentNoticeCatalog
{
    private readonly IReadOnlyList<ConsentNotice> _notices;

    public ConsentNoticeCatalog()
    {
        using var stream = typeof(ConsentNoticeCatalog).Assembly.GetManifestResourceStream(
            "Palladin.Module.Identity.Infrastructure.Consents.notices.json")!;
        _notices = JsonSerializer.Deserialize<ConsentNotice[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    internal ConsentNoticeCatalog(IReadOnlyList<ConsentNotice> notices) => _notices = notices;

    internal ConsentNotice? Current(string purpose, string locale) =>
        _notices.LastOrDefault(notice => notice.Purpose == purpose && notice.Locale == locale);
}

internal sealed class ConsentOptions
{
    public const string Position = "Modules:Identity:Consents";
    public int MaxAgeSeconds { get; init; } = 60;
}
