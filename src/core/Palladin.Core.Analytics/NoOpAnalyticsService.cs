namespace Palladin.Core.Analytics;

public sealed class NoOpAnalyticsService : IAnalyticsService
{
    public void CaptureEvent(string distinctId, string module, string eventName, Dictionary<string, object>? properties = null)
    {
    }

    public Task IdentifyUser(string distinctId, Dictionary<string, object>? properties = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
