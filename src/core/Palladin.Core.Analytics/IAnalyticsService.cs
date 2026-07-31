namespace Palladin.Core.Analytics;

public interface IAnalyticsService
{
    void CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null);
    Task IdentifyUser(string distinctId, Dictionary<string, object>? properties = null, CancellationToken cancellationToken = default);
}
