using Hestia.Platform.Http;

namespace Hestia.Platform.Tests;

/// <summary>The NUL escapes that the host removes from JSON bodies, and the text it must leave as it is.</summary>
public class NulCharactersTests
{
    [Theory]
    [InlineData(@"{""text"":""a\u0000b""}", @"{""text"":""ab""}")]
    [InlineData(@"{""text"":""\u0000\u0000""}", @"{""text"":""""}")]
    // An escaped backslash before NUL: the backslash stays, the NUL goes.
    [InlineData(@"{""text"":""a\\\u0000b""}", @"{""text"":""a\\b""}")]
    // The text "\u0000" (an escaped backslash, then u0000) is not a NUL: it stays.
    [InlineData(@"{""text"":""a\\u0000b""}", @"{""text"":""a\\u0000b""}")]
    [InlineData(@"{""text"":""a\\\\u0000b""}", @"{""text"":""a\\\\u0000b""}")]
    // Other escapes stay.
    [InlineData(@"{""text"":""a\u0001\né""}", @"{""text"":""a\u0001\né""}")]
    public void RemoveFromJson_RemovesOnlyNulEscapes(string json, string expected)
    {
        Assert.Equal(expected, NulCharacters.RemoveFromJson(json));
        Assert.DoesNotContain('\0', System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(expected)!["text"]);
    }

    [Fact]
    public void RemoveFromJson_KeepsTheSameText_WithoutANul()
    {
        const string json = @"{""text"":""plain""}";
        Assert.Same(json, NulCharacters.RemoveFromJson(json));
    }
}
