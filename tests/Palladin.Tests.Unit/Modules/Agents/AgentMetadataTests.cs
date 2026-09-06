using Palladin.Module.Agents.Domain;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Agents;

public sealed class AgentMetadataTests
{
    [Fact]
    public void When_MetadataIsValid_Then_TrimsAndNormalizesNfc()
    {
        AgentMetadata.TryNormalizeDisplayName("  Cafe\u0301  ", out var displayName).ShouldBeTrue();
        AgentMetadata.TryNormalizeType("  custom-runtime  ", out var type).ShouldBeTrue();

        displayName.ShouldBe("Café");
        type.ShouldBe("custom-runtime");
    }

    [Fact]
    public void When_OptionalMetadataIsBlank_Then_TreatsItAsAbsent()
    {
        AgentMetadata.TryNormalizeDisplayName("  ", out var displayName).ShouldBeTrue();
        AgentMetadata.TryNormalizeType(null, out var type).ShouldBeTrue();

        displayName.ShouldBeNull();
        type.ShouldBeNull();
    }

    [Theory]
    [InlineData("safe\u0000unsafe")]
    [InlineData("safe\u202Eunsafe")]
    [InlineData("safe\u2028unsafe")]
    public void When_MetadataContainsInvisibleOrControlCharacters_Then_RejectsIt(string value)
    {
        AgentMetadata.TryNormalizeDisplayName(value, out _).ShouldBeFalse();
        AgentMetadata.TryNormalizeType(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void When_MetadataExceedsItsCodePointLimit_Then_RejectsIt()
    {
        AgentMetadata.TryNormalizeDisplayName(new string('a', 65), out _).ShouldBeFalse();
        AgentMetadata.TryNormalizeType(new string('a', 101), out _).ShouldBeFalse();
    }

    [Fact]
    public void When_MaximumLengthNameExpandsDuringCaseFolding_Then_ReservationKeyStaysBounded()
    {
        var name = new string('\u00df', AgentMetadata.MaxDisplayNameLength);

        AgentMetadata.TryNormalizeRequiredDisplayName(name, out var normalized).ShouldBeTrue();
        AgentMetadata.DisplayNameReservationKey(normalized).Length.ShouldBe(64);
    }
}
