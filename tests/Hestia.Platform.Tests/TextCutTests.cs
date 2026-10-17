using Hestia.Platform.Text;

namespace Hestia.Platform.Tests;

/// <summary>A cut to a length limit, which must not leave half of an emoji.</summary>
public class TextCutTests
{
    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab😀cd", 4, "ab😀")]
    // The limit falls inside the emoji: it goes, not half of it.
    [InlineData("ab😀cd", 3, "ab")]
    [InlineData("😀", 1, "")]
    public void Start_CutsBetweenCharacters(string text, int max, string expected) =>
        Assert.Equal(expected, TextCut.Start(text, max));
}
