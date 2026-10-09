// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/read.ts, write.ts, edit.ts, bash.ts, grep.ts
// and find.ts reach Node's fs and child_process with the model's strings; a NUL byte makes Node throw ERR_INVALID_ARG_VALUE, whose
// message the tool result carries.
using System.Text;

namespace PiSharp.Tools;

/// <summary>Node's ERR_INVALID_ARG_VALUE texts for strings with NUL bytes, with util.inspect's quoting and escaping of the value and
/// internal/errors.js's 128-character cut.</summary>
public static class NodeArgumentErrors
{
    /// <summary>fs: <c>The argument 'path' must be a string, Uint8Array, or URL without null bytes. Received '...'</c>.</summary>
    public static string NullBytePath(string path) =>
        "The argument 'path' must be a string, Uint8Array, or URL without null bytes. Received " + Received(path);

    /// <summary>child_process.spawn: <c>The argument 'args[N]' must be a string without null bytes. Received '...'</c>.</summary>
    public static string NullByteSpawnArgument(int index, string argument) =>
        $"The argument 'args[{index}]' must be a string without null bytes. Received " + Received(argument);

    /// <summary>The spawn error for the first argument with a NUL byte (Node checks them in order), or null.</summary>
    public static string? SpawnArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
            if (arguments[index].Contains('\0')) return NullByteSpawnArgument(index, arguments[index]);
        return null;
    }

    private static string Received(string value)
    {
        var inspected = Inspect(value);
        return inspected.Length > 128 ? inspected[..128] + "..." : inspected;
    }

    /// <summary>util.inspect of a string: single quotes unless the string has one (then double quotes, else backticks when it has
    /// neither a backtick nor "${"), C0/C1 controls and DEL as \xHH (\b \t \n \f \r by name), backslash doubled, lone surrogates \uhhhh.</summary>
    public static string Inspect(string value)
    {
        var quote = '\'';
        if (value.Contains('\''))
        {
            if (!value.Contains('"')) quote = '"';
            else if (!value.Contains('`') && !value.Contains("${", StringComparison.Ordinal)) quote = '`';
        }
        var result = new StringBuilder(value.Length + 2).Append(quote);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character < 0x20 || character is >= '\u007f' and <= '\u009f')
                result.Append(character switch
                {
                    '\b' => "\\b", '\t' => "\\t", '\n' => "\\n", '\f' => "\\f", '\r' => "\\r",
                    _ => "\\x" + ((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
                });
            else if (character == '\\') result.Append("\\\\");
            else if (character == '\'' && quote == '\'') result.Append("\\'");
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                result.Append(character).Append(value[++index]);
            else if (char.IsSurrogate(character))
                result.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else result.Append(character);
        }
        return result.Append(quote).ToString();
    }
}
