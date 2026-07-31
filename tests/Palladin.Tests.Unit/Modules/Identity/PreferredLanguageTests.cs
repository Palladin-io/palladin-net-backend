using Palladin.Module.Identity.Domain;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class PreferredLanguageTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("pl", "pl")]
    [InlineData("PL", "pl")]
    [InlineData("en-US", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("  De  ", "de")]
    [InlineData("fr", "fr")]
    [InlineData("xx-INVALID", "xx")]
    public void When_GivenValidLanguage_Then_NormalizesToIso6391(string input, string expected)
    {
        PreferredLanguage.From(input).Code.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("eng")]
    [InlineData("e")]
    [InlineData("12")]
    [InlineData("e1")]
    [InlineData("!!")]
    [InlineData("invalid")]
    public void When_GivenNullOrInvalidLanguage_Then_FallsBackToDefault(string? input)
    {
        PreferredLanguage.From(input).Code.ShouldBe("en");
    }

    [Fact]
    public void Default_IsEn()
    {
        PreferredLanguage.Default.Code.ShouldBe("en");
        PreferredLanguage.DefaultCode.ShouldBe("en");
    }

    [Fact]
    public void ToString_ReturnsCode()
    {
        PreferredLanguage.From("pl").ToString().ShouldBe("pl");
    }

    [Fact]
    public void Equality_IsByCode()
    {
        PreferredLanguage.From("en-GB").ShouldBe(PreferredLanguage.From("en"));
    }
}
