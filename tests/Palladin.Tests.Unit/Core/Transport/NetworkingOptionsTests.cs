using Palladin.Core.Options;

namespace Palladin.Tests.Unit.Core.Transport;

public sealed class NetworkingOptionsTests
{
    [Theory]
    [InlineData("https://panel.example.test")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://[fd00::20]:8080")]
    public void When_ExactWebOriginIsConfigured_Then_ItIsAccepted(string origin)
    {
        // Given
        var options = new NetworkingOptions { AllowedOrigins = [origin] };

        // When / Then
        Should.NotThrow(options.ValidateAllowedOrigins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    [InlineData("https://user@panel.example.test")]
    [InlineData("https://panel.example.test/path")]
    [InlineData("https://panel.example.test?other")]
    [InlineData("https://panel.example.test#other")]
    [InlineData("ftp://panel.example.test")]
    public void When_OriginIsWildcardOrNotCanonical_Then_StartupConfigurationIsRejected(string origin)
    {
        // Given
        var options = new NetworkingOptions { AllowedOrigins = [origin] };

        // When / Then
        Should.Throw<InvalidOperationException>(options.ValidateAllowedOrigins);
    }
}
