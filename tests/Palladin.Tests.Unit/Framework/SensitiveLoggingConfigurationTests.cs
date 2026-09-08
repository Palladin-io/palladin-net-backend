using System.Text.Json;

namespace Palladin.Tests.Unit.Framework;

public sealed class SensitiveLoggingConfigurationTests
{
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Testing.json")]
    public void HostingDiagnosticsInformationLogs_AreDisabled(string fileName)
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "ApiConfiguration", fileName);
        using var stream = File.OpenRead(configurationPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
        });

        var configuredLevel = document.RootElement
            .GetProperty("Serilog")
            .GetProperty("MinimumLevel")
            .GetProperty("Override")
            .GetProperty("Microsoft.AspNetCore.Hosting.Diagnostics")
            .GetString();

        configuredLevel.ShouldBe("Warning");
    }
}
