using System.Diagnostics;

namespace Palladin.Core.Ai.Tracing;

internal static class ActivitySourceExtensions
{
    public static RootActivity StartRootActivity(this ActivitySource source,
        string name,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        var parent = Activity.Current;
        Activity.Current = null;

        var links = new List<ActivityLink>();
        if (parent != null)
        {
            links.Add(new ActivityLink(parent.Context));
        }

        var next = source.StartActivity(name, kind,
            parentContext: default,
            tags: tags,
            links: links);

        return new RootActivity(next, parent);
    }
}

public sealed class RootActivity(Activity? activity, Activity? parentActivity) : IDisposable
{
    public Activity? Activity { get; } = activity;
    public Activity? ParentActivity { get; } = parentActivity;

    private bool _disposedValue;

    private void Dispose(bool disposing)
    {
        if (_disposedValue) return;

        if (disposing)
        {
            Activity?.Dispose();
            Activity.Current = ParentActivity;
        }

        _disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
