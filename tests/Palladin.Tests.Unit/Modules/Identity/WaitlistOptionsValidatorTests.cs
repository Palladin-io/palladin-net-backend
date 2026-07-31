using Microsoft.Extensions.Hosting;
using NSubstitute;
using Palladin.Module.Identity.Infrastructure.Waitlist;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class WaitlistOptionsValidatorTests
{
    [Fact]
    public void When_Disabled_Then_EmptyConfigurationSucceeds()
    {
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new WaitlistOptions { Enabled = false });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void When_Enabled_AndUrlsMissing_Then_Fails()
    {
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new WaitlistOptions { Enabled = true });

        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(3);
    }

    [Fact]
    public void When_Enabled_AndProductionUrlsUseHttp_Then_Fails()
    {
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, ValidOptions("http"));

        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(3);
    }

    [Fact]
    public void When_Enabled_AndConfigurationValid_Then_Succeeds()
    {
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, ValidOptions("https"));

        result.Succeeded.ShouldBeTrue();
    }

    private static WaitlistOptions ValidOptions(string scheme) => new()
    {
        Enabled = true,
        VerificationUrlBase = $"{scheme}://api.palladin.io/api/waitlist/verify",
        VerifiedRedirectUrl = $"{scheme}://palladin.io/waitlist/confirmed",
        FailedRedirectUrl = $"{scheme}://palladin.io/waitlist/invalid",
    };

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}
