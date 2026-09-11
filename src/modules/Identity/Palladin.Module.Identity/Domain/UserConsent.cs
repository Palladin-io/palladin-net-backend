using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal static class ConsentPurpose
{
    public const string ProductAnalytics = "product_analytics";
    public const string EmailMarketing = "email_marketing";

    public static string Scope(string purpose) => purpose switch
    {
        ProductAnalytics => "palladin_web_mobile",
        EmailMarketing => "palladin_email_news_and_offers",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}

internal sealed class UserConsent
{
    public Guid UserId { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public string Status { get; private set; } = "unknown";
    public uint Revision { get; private set; }
    public uint ActivationRevision { get; private set; }
    public Instant RecordedAt { get; private set; }
    public string NoticeVersion { get; private set; } = string.Empty;
    public string Locale { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;

    private UserConsent() { }

    internal static UserConsent Create(Guid userId, string purpose) => new()
    {
        UserId = userId,
        Purpose = purpose,
        Scope = ConsentPurpose.Scope(purpose),
    };

    internal UserConsentHistory? TryDecide(
        bool granted,
        uint expectedRevision,
        Guid requestId,
        ConsentNotice notice,
        string source,
        Instant now)
    {
        if (Revision != expectedRevision || Revision == uint.MaxValue)
        {
            return null;
        }

        if (requestId == Guid.Empty || notice.Purpose != Purpose || notice.Scope != Scope
            || string.IsNullOrWhiteSpace(notice.Version) || string.IsNullOrWhiteSpace(notice.Text)
            || notice.Locale is not ("pl" or "en")
            || source is not ("web_onboarding" or "web_settings" or "mobile_onboarding" or "mobile_settings"))
        {
            throw new ArgumentException("Invalid consent decision provenance.");
        }

        if (granted && (Status != "granted" || NoticeVersion != notice.Version))
        {
            ActivationRevision = Revision + 1;
        }
        else if (!granted)
        {
            ActivationRevision = 0;
        }

        Status = granted ? "granted" : Status is "granted" or "withdrawn" ? "withdrawn" : "denied";
        Revision++;
        RecordedAt = now;
        NoticeVersion = notice.Version;
        Locale = notice.Locale;
        Source = source;
        return UserConsentHistory.Record(this, requestId, expectedRevision, notice.Text);
    }
}

internal sealed record ConsentNotice(string Purpose, string Scope, string Version, string Locale, string Text);
