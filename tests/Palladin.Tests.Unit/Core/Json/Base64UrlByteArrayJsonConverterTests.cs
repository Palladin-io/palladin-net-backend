using System.Text.Json;
using Palladin.Core.Json;

namespace Palladin.Tests.Unit.Core.Json;

public sealed class Base64UrlByteArrayJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new Base64UrlByteArrayJsonConverter() },
    };

    [Fact]
    public void When_BytesAreSerialized_Then_WireValueIsCanonicalUnpaddedBase64Url()
    {
        var serialized = JsonSerializer.Serialize<byte[]>([0xFB, 0xFF], Options);

        serialized.ShouldBe("\"-_8\"");
        JsonSerializer.Deserialize<byte[]>(serialized, Options).ShouldBe(new byte[] { 0xFB, 0xFF });
    }

    [Theory]
    [InlineData("\"-_8=\"")]
    [InlineData("\"+/8=\"")]
    [InlineData("\"A\"")]
    public void When_ValueIsNotCanonicalBase64Url_Then_DeserializationFails(string json)
    {
        var deserialize = () => JsonSerializer.Deserialize<byte[]>(json, Options);

        deserialize.ShouldThrow<JsonException>();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void When_WireTokenIsNotAString_Then_DeserializationFailsAsJsonError(string json)
    {
        var deserialize = () => JsonSerializer.Deserialize<byte[]>(json, Options);

        deserialize.ShouldThrow<JsonException>();
    }
}
