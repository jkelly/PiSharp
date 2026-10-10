// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/resolve-config-value.ts.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Models;

/// <summary>
/// Source resolve-config-value.ts: <c>!cmd</c> runs a shell command and uses its trimmed stdout (cached for the process by
/// <see cref="Resolve"/>, rerun by <see cref="ResolveUncached"/>); <c>$NAME</c> and <c>${NAME}</c> interpolate the scoped then the
/// process environment; <c>$$</c> and <c>$!</c> escape; anything else is a literal. Owner decision 0004: commands run as in Pi.
/// </summary>
internal sealed class ConfigValueResolver
{
    private abstract record Part;
    private sealed record Literal(string Value) : Part;
    private sealed record Env(string Name) : Part;

    /// <summary>Source commandResultCache: one process-wide cache for the real shell runner.</summary>
    private static readonly ConcurrentDictionary<string, string?> SharedCommandCache = new(StringComparer.Ordinal);
    private readonly Func<string, string?> process;
    private readonly Func<string, string?> runCommand;
    private readonly ConcurrentDictionary<string, string?> commandCache;

    /// <param name="processEnvironment">The process environment reader; an empty value is absent.</param>
    /// <param name="runCommand">A command runner (the command text without <c>!</c>) with its own cache; null runs the platform shell
    /// (<see cref="RunShellCommand(string)"/>) with the process-wide cache.</param>
    internal ConfigValueResolver(Func<string, string?> processEnvironment, Func<string, string?>? runCommand = null)
    {
        process = processEnvironment ?? throw new ArgumentNullException(nameof(processEnvironment));
        this.runCommand = runCommand ?? RunShellCommand;
        commandCache = runCommand is null ? SharedCommandCache : new(StringComparer.Ordinal);
    }

    /// <summary>The process-environment resolver with the real shell runner (auth.json keys, MCP values).</summary>
    internal static ConfigValueResolver Process { get; } = new(Environment.GetEnvironmentVariable);

    internal static bool IsCommand(string config) => config.StartsWith('!');

    private static List<Part> ParseTemplate(string config)
    {
        var parts = new List<Part>(); var index = 0;
        void Append(string value)
        {
            if (value.Length == 0) return;
            if (parts.Count > 0 && parts[^1] is Literal previous) parts[^1] = new Literal(previous.Value + value);
            else parts.Add(new Literal(value));
        }
        while (index < config.Length)
        {
            var dollar = config.IndexOf('$', index);
            if (dollar < 0) { Append(config[index..]); break; }
            Append(config[index..dollar]);
            var next = dollar + 1 < config.Length ? config[dollar + 1] : '\0';
            if (dollar + 1 < config.Length && next is '$' or '!') { Append(next.ToString()); index = dollar + 2; continue; }
            if (dollar + 1 < config.Length && next == '{')
            {
                var end = config.IndexOf('}', dollar + 2);
                if (end < 0) { Append("$"); index = dollar + 1; continue; }
                var name = config[(dollar + 2)..end];
                if (IsName(name)) parts.Add(new Env(name)); else Append(config[dollar..(end + 1)]);
                index = end + 1; continue;
            }
            var length = 0;
            while (dollar + 1 + length < config.Length && (length == 0 ? IsStart(config[dollar + 1]) : IsPart(config[dollar + 1 + length]))) length++;
            if (length > 0) { parts.Add(new Env(config.Substring(dollar + 1, length))); index = dollar + 1 + length; continue; }
            Append("$"); index = dollar + 1;
        }
        return parts;
    }

    private static bool IsStart(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsPart(char value) => IsStart(value) || value is >= '0' and <= '9';
    private static bool IsName(string value) => value.Length != 0 && IsStart(value[0]) && value.All(IsPart);

    private string? EnvValue(string name, IReadOnlyDictionary<string, string>? scoped) =>
        scoped is not null && scoped.TryGetValue(name, out var value) && value.Length != 0 ? value :
        process(name) is { Length: > 0 } fromProcess ? fromProcess : null;

    /// <summary>Source getConfigValueEnvVarName: the variable of a value that is exactly one reference.</summary>
    internal static string? GetEnvVarName(string config)
    {
        if (IsCommand(config)) return null;
        var parts = ParseTemplate(config);
        return parts is [Env single] ? single.Name : null;
    }

    /// <summary>Source getConfigValueEnvVarNames: distinct referenced variables in order.</summary>
    internal static IReadOnlyList<string> GetEnvVarNames(string config) =>
        IsCommand(config) ? [] : ParseTemplate(config).OfType<Env>().Select(part => part.Name).Distinct(StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<string> GetMissingEnvVarNames(string config, IReadOnlyDictionary<string, string>? env = null) =>
        GetEnvVarNames(config).Where(name => EnvValue(name, env) is null).ToArray();

    internal bool IsConfigured(string config, IReadOnlyDictionary<string, string>? env = null) => GetMissingEnvVarNames(config, env).Count == 0;

    private string? ResolveTemplate(string config, IReadOnlyDictionary<string, string>? env)
    {
        var resolved = new StringBuilder();
        foreach (var part in ParseTemplate(config))
        {
            if (part is Literal literal) { resolved.Append(literal.Value); continue; }
            var value = EnvValue(((Env)part).Name, env);
            if (value is null) return null;
            resolved.Append(value);
        }
        return resolved.ToString();
    }

    private string? Execute(string config, bool cached) =>
        cached ? commandCache.GetOrAdd(config, key => runCommand(key[1..])) : runCommand(config[1..]);

    /// <summary>Source resolveConfigValue (successful and failed commands cached until <see cref="ClearCache"/>).</summary>
    internal string? Resolve(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommand(config) ? Execute(config, cached: true) : ResolveTemplate(config, env);

    /// <summary>Source resolveConfigValueUncached.</summary>
    internal string? ResolveUncached(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommand(config) ? Execute(config, cached: false) : ResolveTemplate(config, env);

    /// <summary>Source resolveConfigValueOrThrow (uncached), with the upstream messages.</summary>
    internal string ResolveOrThrow(string config, string description, IReadOnlyDictionary<string, string>? env = null)
    {
        var value = ResolveUncached(config, env);
        if (value is not null) return value;
        if (IsCommand(config)) throw new InvalidOperationException($"Failed to resolve {description} from shell command: {config[1..]}");
        var missing = GetMissingEnvVarNames(config, env);
        if (missing.Count == 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variable: {missing[0]}");
        if (missing.Count > 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variables: {string.Join(", ", missing)}");
        throw new InvalidOperationException($"Failed to resolve {description}");
    }

    /// <summary>Source resolveHeaders: unresolved values are dropped; an empty result is null.</summary>
    internal IReadOnlyDictionary<string, string>? ResolveHeaders(IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers) if (Resolve(value, env) is { Length: > 0 } text) resolved[key] = text;
        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Source resolveHeadersOrThrow.</summary>
    internal IReadOnlyDictionary<string, string>? ResolveHeadersOrThrow(IReadOnlyDictionary<string, string>? headers, string description,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (headers is null) return null;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers) resolved[key] = ResolveOrThrow(value, $"{description} header \"{key}\"", env);
        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Source clearConfigValueCache.</summary>
    internal void ClearCache() => commandCache.Clear();

    /// <summary>
    /// Source executeCommandUncached: on Windows the configured shell first (getShellConfig without a shellPath: Git Bash under
    /// ProgramFiles, then bash on PATH; legacy WSL bash reads the command from standard input) and execSync's default shell only when
    /// that shell cannot start; elsewhere execSync's <c>/bin/sh -c</c>. Ten-second timeout, stdin and stderr ignored; a failure, a
    /// non-zero exit or empty output resolves nothing.
    /// </summary>
    internal static string? RunShellCommand(string command) => RunShellCommand(command, ShellHost.Current);

    internal static string? RunShellCommand(string command, ShellHost host)
    {
        if (!host.IsWindows) return RunDefaultShell(command, windows: false, host);
        var (executed, value) = RunConfiguredShell(command, host);
        return executed ? value : RunDefaultShell(command, windows: true, host);
    }

    /// <summary>Source executeWithConfiguredShell: (executed, value). No shell found or a shell that cannot start (ENOENT) is not
    /// executed; a timeout or any other failure is executed without a value.</summary>
    private static (bool Executed, string? Value) RunConfiguredShell(string command, ShellHost host)
    {
        ShellConfiguration shell;
        try { shell = ShellDiscovery.Resolve(host: host); }
        catch (ShellDiscoveryException) { return (false, null); }
        var info = new ProcessStartInfo(shell.Shell);
        foreach (var argument in shell.CommandArguments(command)) info.ArgumentList.Add(argument);
        try { return (true, Run(info, shell.CommandTransport == ShellCommandTransport.Stdin ? command : null)); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException) { return (false, null); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { return (true, null); }
    }

    /// <summary>Source executeWithDefaultShell (execSync): <c>%ComSpec% /d /s /c "command"</c> on Windows, <c>/bin/sh -c</c> elsewhere.</summary>
    private static string? RunDefaultShell(string command, bool windows, ShellHost host)
    {
        var info = windows
            ? new ProcessStartInfo(host.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        try { return Run(info, null); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException or IOException or InvalidOperationException) { return null; }
    }

    /// <summary>One command process: stdout captured as UTF-8, stderr discarded, stdin closed (or given the command), killed after 10 s.
    /// The trimmed output, or null for a timeout, a non-zero exit or empty output.</summary>
    private static string? Run(ProcessStartInfo info, string? input)
    {
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.RedirectStandardInput = true;
        info.UseShellExecute = false; info.CreateNoWindow = true; info.StandardOutputEncoding = Encoding.UTF8;
        using var child = System.Diagnostics.Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("The command shell did not start.");
        try { if (input is not null) child.StandardInput.Write(input); child.StandardInput.Close(); } catch (IOException) { }
        var output = child.StandardOutput.ReadToEndAsync();
        _ = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(10_000)) { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
        child.WaitForExit();
        if (child.ExitCode != 0) return null;
        var text = output.GetAwaiter().GetResult().Trim();
        return text.Length == 0 ? null : text;
    }
}
