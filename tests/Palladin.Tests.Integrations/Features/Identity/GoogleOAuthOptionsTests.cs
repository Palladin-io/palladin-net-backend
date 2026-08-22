using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Identity.Infrastructure.Options;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

public sealed class GoogleOAuthOptionsTests
{
    [Fact]
    public void When_GoogleClientIdIsEmpty_Then_ConfigurationIsRejected()
    {
        // Given
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GoogleOAuthOptions.Position}:ClientId"] = string.Empty,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddIdentityOAuth(configuration);
        using var provider = services.BuildServiceProvider();

        // When
        var exception = Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GoogleOAuthOptions>>().Value);

        // Then
        exception.Failures.ShouldContain(
            "Google OAuth client ID must be configured.");
    }
}
