using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class PasswordAuthOptionsValidatorTests
{
    [Fact]
    public void When_SecretEmpty_OutsideDevelopment_Then_Fails()
    {
        var validator = new PasswordAuthOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new PasswordAuthOptions { EnumerationSecret = "" });

        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void When_SecretEmpty_InDevelopment_Then_Succeeds()
    {
        var validator = new PasswordAuthOptionsValidator(Environment("Development"));

        var result = validator.Validate(null, new PasswordAuthOptions { EnumerationSecret = "" });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void When_SecretSet_OutsideDevelopment_Then_Succeeds()
    {
        var validator = new PasswordAuthOptionsValidator(Environment("Production"));

        var result = validator.Validate(null, new PasswordAuthOptions { EnumerationSecret = "a-real-secret" });

        result.Succeeded.ShouldBeTrue();
    }

    private static IHostEnvironment Environment(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }
}
