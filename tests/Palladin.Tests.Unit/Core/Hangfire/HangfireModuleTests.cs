using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Palladin.Core.Hangfire;

namespace Palladin.Tests.Unit.Core.Hangfire;

public sealed class HangfireModuleTests
{
    [Fact]
    public void When_HangfireIsDisabled_Then_DoesNotRegisterStorageOrWorkers()
    {
        // Given
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Enabled"] = "false",
                ["ConnectionString"] = "not-used"
            })
            .Build();
        var services = new ServiceCollection();

        // When
        services.AddHangfireModule(configuration);

        // Then
        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IBackgroundJobClient));
        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }
}
