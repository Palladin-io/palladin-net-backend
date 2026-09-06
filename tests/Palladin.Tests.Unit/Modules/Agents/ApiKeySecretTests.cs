using System.Text;
using Palladin.Module.Agents.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Agents;

public sealed class ApiKeySecretTests
{
    [Fact]
    public void GeneratedSecret_UsesTheCanonicalPrefixAndBase64UrlAlphabet()
    {
        using var secret = GeneratedApiKeySecret.Generate();
        var value = Encoding.ASCII.GetString(secret.Bytes);

        value.Length.ShouldBe(46);
        value.ShouldStartWith("pl_");
        value[3..].ShouldAllBe(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_');
    }

    [Fact]
    public void DisposedSecret_DropsItsExposedBuffer()
    {
        var secret = GeneratedApiKeySecret.Generate();
        secret.Bytes.Length.ShouldBeGreaterThan(0);

        secret.Dispose();

        secret.Bytes.Length.ShouldBe(0);
    }
}
