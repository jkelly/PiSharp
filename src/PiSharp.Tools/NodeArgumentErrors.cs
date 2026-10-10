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

    /// <summary>child_process.spawn's normalizeSpawnArguments null-byte checks, in Node's order: <c>file</c>, each <c>args[N]</c>,
    /// <c>options.cwd</c>, then each <c>options.env</c> key and value. Node throws before anything is spawned (Node 22 texts, checked
    /// with node). Null when no string has a NUL byte.</summary>
    public static string? SpawnNullBytes(string file, IReadOnlyList<string> arguments, string? workingDirectory,
        IEnumerable<KeyValuePair<string, string>>? environment)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(arguments);
        if (file.Contains('\0')) return "The argument 'file' must be a string without null bytes. Received " + Received(file);
        if (SpawnArguments(arguments) is { } argument) return argument;
        if (workingDirectory is not null && workingDirectory.Contains('\0'))
            return "The property 'options.cwd' must be a string, Uint8Array, or URL without null bytes. Received " + Received(workingDirectory);
        if (environment is not null)
            foreach (var (key, value) in environment)
            {
                if (key.Contains('\0')) return $"The property 'options.env['{key}']' must be a string without null bytes. Received " + Received(key);
                if (value is not null && value.Contains('\0'))
                    return $"The property 'options.env['{key}']' must be a string without null bytes. Received " + Received(value);
            }
        return null;
    }

    /// <summary>child_process.spawn's error when the operating system refuses the command line: on Windows CreateProcess takes at most
    /// 32,767 characters (libuv reports ENAMETOOLONG); on Unix one argument is at most 128 KiB (E2BIG). Null when it fits.</summary>
    public static string? SpawnLimit(string executable, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
            return Processes.WindowsProcessLifetime.CommandLineLength(executable, arguments) > 32_766 ? "spawn ENAMETOOLONG" : null;
        return arguments.Any(argument => Encoding.UTF8.GetByteCount(argument) + 1 > 128 * 1024) ? "spawn E2BIG" : null;
    }

    /// <summary>child_process.spawn's failure for a libuv error code (internal/child_process.js ChildProcess.spawn): EACCES, EAGAIN,
    /// EMFILE, ENFILE and ENOENT are run-time errors emitted as the child's <c>error</c> event, <c>spawn &lt;file&gt; &lt;code&gt;</c>; any other
    /// code is thrown at once as <c>spawn &lt;code&gt;</c> (checked with Node 22: <c>spawn EPERM</c>, <c>spawn EFTYPE</c>, <c>spawn UNKNOWN</c>).</summary>
    public static string SpawnFailure(string file, string code) =>
        code is "EACCES" or "EAGAIN" or "EMFILE" or "ENFILE" or "ENOENT" ? $"spawn {file} {code}" : "spawn " + code;

    /// <summary>libuv's uv_translate_sys_error (src/win/error.c, libuv 1.51 as bundled with Node 22) for the Win32 errors CreateProcess and
    /// libuv's path search can report; anything else is <c>UNKNOWN</c>.</summary>
    public static string WindowsErrorCode(int error) => error switch
    {
        2 or 3 or 15 or 123 or 126 or 161 or 203 or 267 or 4392 => "ENOENT",
        5 or 1314 => "EPERM",
        740 or 1920 => "EACCES",
        8 or 14 => "ENOMEM",
        4 => "EMFILE",
        6 or 1004 => "EBADF",
        13 or 87 or 122 or 1464 => "EINVAL",
        32 or 33 or 231 => "EBUSY",
        31 or 110 => "EIO",
        50 => "ENOTSUP",
        111 or 206 => "ENAMETOOLONG",
        193 => "EFTYPE",
        998 => "EFAULT",
        1 => "EISDIR",
        1113 => "ECHARSET",
        1921 => "ELOOP",
        _ => "UNKNOWN"
    };

    /// <summary>The libuv code of a Linux or macOS errno (uv-errno.h names the platform's own values); anything else is <c>UNKNOWN</c>.</summary>
    public static string UnixErrorCode(int errno, bool macOS) => errno switch
    {
        1 => "EPERM", 2 => "ENOENT", 5 => "EIO", 7 => "E2BIG", 8 => "ENOEXEC", 12 => "ENOMEM", 13 => "EACCES", 14 => "EFAULT",
        20 => "ENOTDIR", 21 => "EISDIR", 22 => "EINVAL", 23 => "ENFILE", 24 => "EMFILE", 26 => "ETXTBSY", 30 => "EROFS",
        11 when !macOS => "EAGAIN", 35 when macOS => "EAGAIN",
        36 when !macOS => "ENAMETOOLONG", 63 when macOS => "ENAMETOOLONG",
        40 when !macOS => "ELOOP", 62 when macOS => "ELOOP",
        _ => "UNKNOWN"
    };

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
