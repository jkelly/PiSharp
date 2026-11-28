// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/src/program-resume.ts.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Tui.Pi;

public sealed record ProgramResumeCommand(IReadOnlyList<string> Argv, string? Cwd = null);
public sealed record ProgramResumeSender(string Token, int Pid, long Time);

/// <summary>Formats authenticated OSC 7577 terminal resume reports.</summary>
public static class ProgramResume
{
    private const string TokenVariable = "OSC7577_TOKEN";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string? TakeProgramResumeToken()
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, null);
        return string.IsNullOrEmpty(token) ? null : token;
    }

    public static string? FormatProgramResume(ProgramResumeCommand command, ProgramResumeSender sender)
    {
        if (command.Argv.Count is 0 or > 64) return null;
        var program = command.Argv[0];
        if (program.Length == 0 || !IsAbsolute(program) &&
            (program.StartsWith('-') || program.IndexOfAny(['/', '\\']) >= 0)) return null;
        if (command.Cwd is { } cwd && !IsAbsolute(cwd)) return null;
        if (command.Argv.Any(text => !Reportable(text)) || command.Cwd is { } directory && !Reportable(directory)) return null;

        var body = "argv=" + string.Join(',', command.Argv.Select(Encode));
        if (command.Cwd is { } path) body += ":cwd=" + Encode(path);
        var result = FormatReport(body, sender);
        return result.Length <= 2048 ? result : null; // The wire sequence is entirely ASCII.
    }

    public static string FormatProgramResumeClear(ProgramResumeSender sender) => FormatReport("clear=1", sender);

    // node:path accepts rooted Windows paths as absolute, including a single leading slash.
    private static bool IsAbsolute(string path) => Path.IsPathFullyQualified(path) ||
        OperatingSystem.IsWindows() && path.Length > 0 && path[0] is '/' or '\\';

    private static bool Reportable(string text)
    {
        if (text.Length > 2048 || text.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(text) <= 2048; }
        catch (EncoderFallbackException) { return false; }
    }

    private static string Encode(string text) => Convert.ToBase64String(StrictUtf8.GetBytes(text));

    private static string FormatReport(string fields, ProgramResumeSender sender)
    {
        var body = string.Create(CultureInfo.InvariantCulture, $"pid={sender.Pid}:time={sender.Time}:{fields}");
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body + ":" + sender.Token)));
        return $"\u001b]7577;{body}:mac={signature}\u001b\\";
    }
}
