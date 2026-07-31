namespace Palladin.Core.Transport;

public static class CustomHeaders
{
    public const string Prefix = "x-";

    public const string SessionIdHeaderName = "x-session-id";
    public const string CorrelationIdHeaderName = "x-correlation-id";
    public const string PlatformHeaderName = "x-platform";
    public const string UserAgentHeaderName = "x-user-agent";
    public const string AppVersionHeaderName = "x-app-version";
    public const string AppBuildNumberHeaderName = "x-app-build-number";
    public const string FeatureFlagPrefix = "x-ff-";
}
