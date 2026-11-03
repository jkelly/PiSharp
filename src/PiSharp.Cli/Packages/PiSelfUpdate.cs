// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/config.ts (getConfiguredSelfUpdateArgv,
// makeConfiguredSelfUpdateCommand, makeSelfUpdateCommandStep, detectInstallChange) and src/package-manager-cli.ts (getSelfUpdatePlan,
// runSelfUpdate, runConfiguredSelfUpdate, readVersionOnPath). Decision 0013 (Self-update): the release check asks NuGet for
// PiSharp.Cli, never pi.dev, so {packageName} is PiSharp.Cli and {installSpec} PiSharp.Cli@<version>.
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

/// <summary>Source SelfUpdateCommand (one step): the program, its arguments and the display form.</summary>
internal sealed record SelfUpdateCommand(string Command, IReadOnlyList<string> Args, string Display);

/// <summary>Source InstallChange: <c>removed</c>, or <c>updated</c> with the version now on disk.</summary>
internal sealed record InstallChange(string Kind, string? Version = null);

/// <summary>The user-configured self-update command of <c>pisharp update</c>.</summary>
internal static partial class PiSelfUpdate
{
    public const string EnvironmentVariable = "PI_SELF_UPDATE_COMMAND";
    public const string SettingName = "selfUpdateCommand";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s")]
    private static partial Regex HasWhitespace();

    [GeneratedRegex(@"\{(packageName|version|installSpec)\}")]
    private static partial Regex Placeholder();

    /// <summary>Source getConfiguredSelfUpdateArgv: <c>PI_SELF_UPDATE_COMMAND</c> split on whitespace (no quoting), else the global
    /// <c>selfUpdateCommand</c> argv; null when neither is set. A setting that is not a non-empty array of strings is not a command.</summary>
    public static IReadOnlyList<string>? GetConfiguredSelfUpdateArgv(string? environmentValue, JsonNode? setting)
    {
        var fromEnvironment = Whitespace().Split(PiArgs.JsTrim(environmentValue ?? "")).Where(part => part.Length > 0).ToArray();
        if (fromEnvironment.Length > 0) return fromEnvironment;
        if (setting is not JsonArray { Count: > 0 } array) return null;
        var argv = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
            argv.Add(text);
        }
        return argv;
    }

    /// <summary>Source makeConfiguredSelfUpdateCommand: <c>{packageName}</c>, <c>{version}</c> and <c>{installSpec}</c>
    /// (<c>packageName@version</c>) filled in every argument.</summary>
    public static SelfUpdateCommand MakeConfiguredSelfUpdateCommand(IReadOnlyList<string> argv, string packageName, string version)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["packageName"] = packageName, ["version"] = version, ["installSpec"] = $"{packageName}@{version}",
        };
        var filled = argv.Select(arg => Placeholder().Replace(arg, match => values[match.Groups[1].Value])).ToArray();
        return Step(filled.Length > 0 ? filled[0] : "", filled.Skip(1).ToArray());
    }

    /// <summary>Source makeSelfUpdateCommandStep: the display quotes an argument that contains whitespace.</summary>
    public static SelfUpdateCommand Step(string command, IReadOnlyList<string> args) =>
        new(command, args, string.Join(" ", new[] { command }.Concat(args).Select(arg => HasWhitespace().IsMatch(arg) ? $"\"{arg}\"" : arg)));

    /// <summary>Source detectInstallChange over the PiSharp.Cli assembly this process started from (the package.json analog): <c>removed</c>
    /// when the file is gone, <c>updated</c> when its product version differs from <paramref name="runningVersion"/>, else null. A
    /// single-file build has no assembly path (the Bun binary case) and reports nothing.</summary>
    public static InstallChange? DetectInstallChange(string? assemblyPath, string runningVersion)
    {
        if (string.IsNullOrEmpty(assemblyPath)) return null;
        if (!File.Exists(assemblyPath)) return new("removed");
        string? installed;
        try { installed = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (installed is { Length: > 0 } && installed.IndexOf('+') is var plus and >= 0) installed = installed[..plus];
        return installed is { Length: > 0 } && installed != runningVersion ? new("updated", installed) : null;
    }

    /// <summary>Source readVersionOnPath: the version <c>pisharp --version</c> prints as resolved from PATH (the PiSharp the user runs
    /// next), with <c>PI_PACKAGE_DIR</c> cleared so the new PiSharp does not report this installation; null when it fails.</summary>
    public static async Task<string?> ReadVersionOnPathAsync(PiPackageProcesses processes, CancellationToken cancellationToken)
    {
        try
        {
            var output = await processes.CaptureAsync(PiConfig.DisplayName, ["--version"], null, null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["PI_PACKAGE_DIR"] = "" }, cancellationToken).ConfigureAwait(false);
            return output.Length > 0 ? output : null;
        }
        catch (PiPackageException)
        {
            return null;
        }
    }
}
