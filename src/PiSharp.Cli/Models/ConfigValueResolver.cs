// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/resolve-config-value.ts.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Models;

/// <summary>
/// The single switch for <c>!command</c> configuration values (models.json <c>apiKey</c> and header values). Upstream runs the
/// command through a shell and uses its trimmed stdout. PiSharp's policy refuses stored commands; this switch keeps that
/// refusal until the owner decides otherwise. Flipping it to <see langword="true"/> enables the upstream behaviour everywhere
/// this resolver is used.
/// </summary>
internal static class ConfigValueCommands
{
    internal const bool RunByDefault = false;
}

/// <summary>A config value that PiSharp's policy refuses to run.</summary>
internal sealed class ConfigValueCommandRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Source resolve-config-value.ts: <c>!cmd</c> runs a shell command (when allowed) and uses its trimmed stdout (cached per process
/// for <see cref="Resolve"/>); <c>$NAME</c> and <c>${NAME}</c> interpolate the scoped then the process environment; <c>$$</c> and
/// <c>$!</c> escape; anything else is a literal.
/// </summary>
internal sealed class ConfigValueResolver
{
    private abstract record Part;
    private sealed record Literal(string Value) : Part;
    private sealed record Env(string Name) : Part;

    private readonly Func<string, string?> process;
    private readonly bool runCommands;
    private readonly Func<string, string?> runCommand;
    private readonly ConcurrentDictionary<string, string?> commandCache = new(StringComparer.Ordinal);

    /// <param name="processEnvironment">The process environment reader; an empty value is absent.</param>
    /// <param name="runCommands">Null takes <see cref="ConfigValueCommands.RunByDefault"/>.</param>
    /// <param name="runCommand">The command runner (the command text without <c>!</c>); defaults to the platform shell.</param>
    internal ConfigValueResolver(Func<string, string?> processEnvironment, bool? runCommands = null, Func<string, string?>? runCommand = null)
    {
        process = processEnvironment ?? throw new ArgumentNullException(nameof(processEnvironment));
        this.runCommands = runCommands ?? ConfigValueCommands.RunByDefault;
        this.runCommand = runCommand ?? RunShellCommand;
    }

    internal bool RunsCommands => runCommands;

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

    private string? Execute(string config, bool cached)
    {
        if (!runCommands)
            throw new ConfigValueCommandRefusedException("PiSharp does not run shell commands for config values; store the value or an environment reference instead.");
        return cached ? commandCache.GetOrAdd(config, key => runCommand(key[1..])) : runCommand(config[1..]);
    }

    /// <summary>Source resolveConfigValue (commands cached for the resolver's lifetime).</summary>
    internal string? Resolve(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommand(config) ? Execute(config, cached: true) : ResolveTemplate(config, env);

    /// <summary>Source resolveConfigValueUncached.</summary>
    internal string? ResolveUncached(string config, IReadOnlyDictionary<string, string>? env = null) =>
        IsCommand(config) ? Execute(config, cached: false) : ResolveTemplate(config, env);

    /// <summary>Source resolveConfigValueOrThrow, with the upstream messages. A refused command throws
    /// <see cref="ConfigValueCommandRefusedException"/> naming the value's description.</summary>
    internal string ResolveOrThrow(string config, string description, IReadOnlyDictionary<string, string>? env = null)
    {
        string? value;
        try { value = ResolveUncached(config, env); }
        catch (ConfigValueCommandRefusedException error)
        { throw new ConfigValueCommandRefusedException($"Failed to resolve {description} from shell command: {error.Message}"); }
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

    /// <summary>execSync with the platform default shell (<c>/bin/sh -c</c>, or <c>cmd.exe /d /s /c</c> on Windows): 10 s timeout,
    /// stdin and stderr ignored, a non-zero exit or empty output resolves nothing.</summary>
    internal static string? RunShellCommand(string command)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe")
            { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.RedirectStandardInput = true;
        info.UseShellExecute = false; info.CreateNoWindow = true; info.StandardOutputEncoding = Encoding.UTF8;
        try
        {
            using var child = Process.Start(info);
            if (child is null) return null;
            child.StandardInput.Close();
            var output = child.StandardOutput.ReadToEndAsync();
            _ = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(10_000)) { try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return null; }
            if (child.ExitCode != 0) return null;
            var text = output.GetAwaiter().GetResult().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
