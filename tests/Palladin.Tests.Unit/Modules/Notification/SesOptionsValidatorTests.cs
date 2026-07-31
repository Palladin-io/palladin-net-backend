using Microsoft.Extensions.Hosting;
using NSubstitute;
using Palladin.Module.Notification.Infrastructure.Email;

namespace Palladin.Tests.Unit.Modules.Notification;

public sealed class SesOptionsValidatorTests
{
    [Fact]
    public void When_SenderEmpty_InProduction_Then_Fails()
    {
        var validator = new SesOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new SesOptions());

        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void When_SenderEmpty_InDevelopment_Then_Succeeds()
    {
        var validator = new SesOptionsValidator(Environment("Development"));

        var result = validator.Validate(null, new SesOptions());

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("no-reply@palladin.io", true)]
    [InlineData("not-an-email", false)]
    public void When_SenderConfigured_Then_ValidatesEmail(string fromAddress, bool expectedSuccess)
    {
        var validator = new SesOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new SesOptions { FromAddress = fromAddress });

        result.Succeeded.ShouldBe(expectedSuccess);
    }

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}
