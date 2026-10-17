namespace Hestia.Platform.Text;

/// <summary>
/// Cuts a text to a length limit without splitting a character that takes two UTF-16 units (an emoji, for example): half
/// of one is not valid text, and PostgreSQL and JSON refuse it or change it to "?".
/// </summary>
public static class TextCut
{
    /// <summary>
    /// The start of the text, at most <paramref name="max"/> UTF-16 units (one fewer when the last one would be half of a
    /// character). The same instance when it is not longer.
    /// </summary>
    public static string Start(string text, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        if (text.Length <= max)
        {
            return text;
        }
        return max > 0 && char.IsHighSurrogate(text[max - 1]) && char.IsLowSurrogate(text[max]) ? text[..(max - 1)] : text[..max];
    }
}
