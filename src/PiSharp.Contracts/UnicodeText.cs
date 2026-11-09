namespace PiSharp.Contracts;

/// <summary>UTF-16 bounded slices that never cut a valid surrogate pair in half.</summary>
public static class UnicodeText
{
    public static string SliceHead(string text, int maxCodeUnits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCodeUnits);
        if (text.Length <= maxCodeUnits) return text;
        var end = maxCodeUnits;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        return text[..end];
    }

    public static string SliceTail(string text, int maxCodeUnits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCodeUnits);
        if (text.Length <= maxCodeUnits) return text;
        var start = text.Length - maxCodeUnits;
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
        return text[start..];
    }
}
