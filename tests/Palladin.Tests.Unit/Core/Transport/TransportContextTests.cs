using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Transport;

namespace Palladin.Tests.Unit.Core.Transport;

public sealed class TransportContextTests
{
    private static ITransportContext CreateFilled(params (string Key, string Value)[] headers)
    {
        var context = new ServiceCollection()
            .AddTransportModule()
            .BuildServiceProvider()
            .GetRequiredService<ITransportContext>();

        context.Fill(headers.ToDictionary(h => h.Key, h => h.Value));

        return context;
    }

    [Theory]
    [InlineData("web")]
    [InlineData("mobile")]
    public void When_PlatformHeaderPresent_Then_ReturnsItsValue(string platform)
    {
        // Given
        var context = CreateFilled((CustomHeaders.PlatformHeaderName, platform));

        // When / Then
        context.Platform.ShouldBe(platform);
    }

    [Fact]
    public void When_PlatformHeaderMissing_Then_PlatformIsNull()
    {
        // Given
        var context = CreateFilled();

        // When / Then
        context.Platform.ShouldBeNull();
    }

    [Fact]
    public void When_PlatformHeaderBlank_Then_PlatformIsNull()
    {
        // Given
        var context = CreateFilled((CustomHeaders.PlatformHeaderName, "   "));

        // When / Then
        context.Platform.ShouldBeNull();
    }
}
