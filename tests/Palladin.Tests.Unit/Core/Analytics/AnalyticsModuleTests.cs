using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Palladin.Core.Analytics;
using Palladin.Core.Transport;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Palladin.Tests.Unit.Core.Analytics;

public sealed class AnalyticsModuleTests
{
    [Fact]
    public void When_ProjectApiKeyMissing_Then_ResolvesNoOpAnalytics()
    {
        // Given
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        // When
        builder.AddAnalyticsModule();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        // Then
        scope.ServiceProvider.GetRequiredService<IAnalyticsService>().ShouldBeOfType<NoOpAnalyticsService>();
    }

    [Fact]
    public void When_ProjectApiKeyConfigured_Then_ResolvesPostHogAnalytics()
    {
        // Given
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration["PostHog:ProjectApiKey"] = "phc_test";
        builder.Services.AddSingleton(Substitute.For<ITransportContext>());
        builder.Services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0, 0)));

        // When
        builder.AddAnalyticsModule();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        // Then
        scope.ServiceProvider.GetRequiredService<IAnalyticsService>().ShouldBeOfType<PostHogAnalyticsService>();
    }
}
