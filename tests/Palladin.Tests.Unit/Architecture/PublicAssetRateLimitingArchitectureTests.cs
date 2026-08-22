namespace Palladin.Tests.Unit.Architecture;

public sealed class PublicAssetRateLimitingArchitectureTests
{
    [Fact]
    public void WebsiteIconEnsure_ShouldNotUseTheGlobalRequestRateLimiter()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Palladin.Api",
            "Framework",
            "RateLimitingExtensions.cs"));

        source.ShouldNotContain("/api/public-assets/website-icons/ensure");
        source.ShouldNotContain("public-assets-ensure:member:");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Palladin.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the backend repository root.");
    }
}
