using System.Text;

namespace PiSharp.Cli.Interactive;

/// <summary>Cooked-input draft data only; never owns a canonical message or queued Agent input.</summary>
internal sealed class ChatEditor
{
    internal const int MaximumCharacters = 65_536;
    internal string Text { get; private set; } = "";
    internal bool IsEditing { get; private set; }
    private bool hasLine;
    internal void Begin() => IsEditing = true;
    internal void Set(string text) { Validate(text); Text = text; hasLine = text.Length != 0; }
    internal void Append(string line) { Set(hasLine ? Text + "\n" + line : line); hasLine = true; }
    internal string Take() { var result = Text; Clear(); return result; }
    internal void Clear() { Text = ""; IsEditing = false; hasLine = false; }
    internal static void Validate(string value)
    {
        if (value.Length > MaximumCharacters) throw new InvalidOperationException("Interactive input exceeds its bounded profile.");
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw new InvalidOperationException("Interactive input has invalid Unicode."); }
            else if (char.IsLowSurrogate(value[index])) throw new InvalidOperationException("Interactive input has invalid Unicode.");
    }
    internal static string Display(string value)
    {
        var rendered = new StringBuilder();
        foreach (var character in value)
        {
            var width = character != '\n' && (char.IsControl(character) || character is '\u2028' or '\u2029') ? 6 : 1;
            if (rendered.Length > 8 * 1024 * 1024 - width)
                throw new InvalidOperationException("Interactive rendered view exceeds its bounded profile.");
            if (character == '\n') rendered.Append('\n');
            else if (char.IsControl(character) || character is '\u2028' or '\u2029')
                rendered.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else rendered.Append(character);
        }
        return rendered.ToString();
    }
}
