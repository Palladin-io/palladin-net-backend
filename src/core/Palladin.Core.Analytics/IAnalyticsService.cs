namespace Palladin.Core.Analytics;

public interface IAnalyticsService
{
    void CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null);
}
