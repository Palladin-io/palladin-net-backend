using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class UserConsentHistory
{
    public Guid UserId { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public uint Revision { get; private set; }
    public uint ExpectedRevision { get; private set; }
    public Guid RequestId { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public Instant RecordedAt { get; private set; }
    public string NoticeVersion { get; private set; } = string.Empty;
    public string NoticeText { get; private set; } = string.Empty;
    public string Locale { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;

    private UserConsentHistory() { }

    internal static UserConsentHistory Record(UserConsent consent, Guid requestId, uint expectedRevision, string noticeText) => new()
    {
        UserId = consent.UserId,
        Purpose = consent.Purpose,
        Scope = consent.Scope,
        Revision = consent.Revision,
        ExpectedRevision = expectedRevision,
        RequestId = requestId,
        Status = consent.Status,
        RecordedAt = consent.RecordedAt,
        NoticeVersion = consent.NoticeVersion,
        NoticeText = noticeText,
        Locale = consent.Locale,
        Source = consent.Source,
    };

    internal bool Matches(bool granted, uint expectedRevision, string noticeVersion, string locale, string source) =>
        (Status == "granted") == granted && ExpectedRevision == expectedRevision
        && NoticeVersion == noticeVersion && Locale == locale && Source == source;
}
