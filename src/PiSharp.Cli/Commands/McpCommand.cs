// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/cli.ts (HELP, parseOptions,
// runMcpCommand login/logout, login, waitForRedirectUrl) and packages/coding-agent/src/core/resolve-config-value.ts (templates).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Commands;

/// <summary>Collaborators of `mcp`; tests replace them with fakes and isolated stores.</summary>
internal sealed record McpCommandOptions(string Cwd, string AgentDirectory)
{
    /// <summary>Defaults to `mcp-auth.json` in the agent directory.</summary>
    public IMcpOAuthCredentialBackend? Credentials { get; init; }
    /// <summary>Defaults to a new socket handler; requests to authorization servers go through it.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Defaults to the platform browser.</summary>
    public Action<string>? OpenUrl { get; init; }
    /// <summary>The pasted redirect URL. Defaults to a terminal prompt when standard input is a terminal and the browser
    /// is the default one; otherwise only the browser callback finishes the sign-in.</summary>
    public Func<CancellationToken, Task<string?>>? ReadRedirectUrl { get; init; }
    /// <summary>Environment lookup for `${NAME}` in `oauth.clientSecret`.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
}

/// <summary>`mcp`: sign in to OAuth MCP servers and sign out without starting a session. Agents run it through bash to
/// start an OAuth sign-in; the user only approves access in the browser. Running sessions pick up new credentials on
/// their next turn. Ported subcommands: `login` and `logout` (the original also has `add`, `remove` and `list`).</summary>
internal static class McpCommand
{
    internal const string AppName = "PiSharp.Cli";
    public const string Usage = "mcp login <server> [--timeout <seconds>]; mcp logout <server>; mcp --help";
    internal const int DefaultLoginTimeoutSeconds = 300;
    internal static readonly string Help = string.Join('\n',
        "Usage:",
        $"  {AppName} mcp login <server> [--timeout <seconds>]",
        $"  {AppName} mcp logout <server>",
        "",
        "Sign in to OAuth MCP servers without starting a session.",
        "Reads ~/.pi/agent/mcp.json.",
        "",
        "Commands:",
        "  login <server>          Sign in through the browser",
        "  logout <server>         Delete the stored OAuth credentials",
        "",
        "Options:",
        $"  --timeout <seconds>     How long login waits for the browser (default: {DefaultLoginTimeoutSeconds})");
    private static readonly string HelpHint = $"Use \"{AppName} mcp --help\" for usage.";

    /// <summary>The agent directory: `PI_CODING_AGENT_DIR`, else `~/.pi/agent`.</summary>
    internal static McpCommandOptions DefaultOptions()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["PI_CODING_AGENT_DIR"] = System.Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") };
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return new(Directory.GetCurrentDirectory(), TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(home, platform, environment));
    }

    /// <summary>Run `mcp &lt;args&gt;` and return the exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, McpCommandOptions options,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(output); ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(options);
        void Log(string line) => output.WriteLine(line);
        void Error(string line) => error.WriteLine(line);
        var command = args.Length == 0 ? null : args[0];
        if (command is null or "help" || args.Contains("--help") || args.Contains("-h")) { Log(Help); return 0; }
        if (command is not ("login" or "logout")) { Error($"Unknown mcp command \"{command}\".\n{HelpHint}"); return 1; }

        var globalConfig = Path.Combine(options.AgentDirectory, "mcp.json");
        var projectConfig = Path.Combine(options.Cwd, ".pi", "mcp.json");
        McpLoadedConfiguration loaded;
        try { loaded = McpConfigurationReader.Load(File.Exists(globalConfig) ? new(globalConfig, File.ReadAllText(globalConfig)) : null, null, false); }
        catch (IOException readError) { Error($"Could not read {globalConfig}: {readError.Message}"); return 1; }
        // PiSharp reads no project trust store, so the project's mcp.json is never used.
        var untrustedNote = File.Exists(projectConfig) ? $"{projectConfig} is ignored because PiSharp does not read project trust." : null;

        var parsed = ParseOptions(args[1..], command == "login" ? new() { ["timeout"] = true } : [], Error);
        if (parsed is null) return 1;
        var (positional, values) = parsed.Value;
        if (positional.Count != 1) { Error($"Usage: {AppName} mcp {command} <server>\n{HelpHint}"); return 1; }
        var name = positional[0];
        var entry = loaded.Servers.FirstOrDefault(server => server.Name == name);
        if (entry is null)
        {
            var configured = loaded.Servers.IsEmpty ? "none" : string.Join(", ", loaded.Servers.Select(server => server.Name));
            Error($"No MCP server named \"{name}\".{(untrustedNote is null ? "" : " " + untrustedNote)} Configured: {configured}.");
            return 1;
        }
        if (!McpConfigurationReader.UsesOAuth(entry.Config) || !entry.Config.Raw.Value.TryGetProperty("url", out var configuredUrl) ||
            !Uri.TryCreate(configuredUrl.GetString(), UriKind.Absolute, out var url))
        {
            Error($"MCP server \"{name}\" does not use OAuth. Only HTTP servers without an Authorization header do.");
            return 1;
        }
        var credentials = new McpOAuthCredentialStore(options.Credentials ?? McpOAuthFileCredentialBackend.InAgentDirectory(options.AgentDirectory));
        if (command == "logout")
        {
            Log(credentials.Remove(name, url) ? $"Signed out of MCP server \"{name}\"." : $"No stored credentials for MCP server \"{name}\".");
            return 0;
        }
        var timeoutText = values.GetValueOrDefault("timeout") ?? DefaultLoginTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        if (!double.TryParse(timeoutText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
        {
            Error("--timeout must be a positive number of seconds.");
            return 1;
        }
        return await LoginAsync(entry, url, seconds, credentials, options, Log, Error, token).ConfigureAwait(false);
    }

    /// <summary>`--name value` options; null after reporting an unknown or incomplete one. `--` ends the options.</summary>
    private static (List<string> Positional, Dictionary<string, string> Values)? ParseOptions(string[] args,
        Dictionary<string, bool> known, Action<string> error)
    {
        var positional = new List<string>(); var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--") { positional.AddRange(args[(index + 1)..]); break; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { positional.Add(arg); continue; }
            var name = arg[2..];
            if (!known.ContainsKey(name)) { error($"Unknown option {arg}.\n{HelpHint}"); return null; }
            if (index + 1 >= args.Length) { error($"{arg} needs a value."); return null; }
            values[name] = args[++index];
        }
        return (positional, values);
    }

    private static async Task<int> LoginAsync(McpServerEntry entry, Uri url, double seconds, McpOAuthCredentialStore credentials,
        McpCommandOptions options, Action<string> log, Action<string> error, CancellationToken token)
    {
        var name = entry.Name;
        McpOAuthSettings settings;
        try { settings = McpOAuthSettings.From(entry, (value, description) => ResolveConfigValue(value, description, options.Environment)); }
        catch (InvalidOperationException resolveError) { error($"Sign-in to MCP server \"{name}\" failed: {resolveError.Message}"); return 1; }
        var openUrl = options.OpenUrl ?? OpenBrowser;
        var interactive = options.ReadRedirectUrl is null && options.OpenUrl is null && !Console.IsInputRedirected;
        var readRedirectUrl = options.ReadRedirectUrl ?? (signal => WaitForRedirectUrlAsync(signal, interactive));
        using var handler = options.HttpHandler is null ? new SocketsHttpHandler() : null;
        using var client = new HttpClient(options.HttpHandler ?? handler!, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        // The timeout covers the whole sign-in, like the original's AbortSignal.timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(seconds * 1000, uint.MaxValue - 1d)));
        try
        {
            await McpSignIn.SignInAsync(new(url, credentials.ForServer(name, url), settings, new(authorizationUrl =>
            {
                log($"Sign in to MCP server \"{name}\" in your browser:\n{authorizationUrl.AbsoluteUri}");
                openUrl(authorizationUrl.AbsoluteUri);
            }, readRedirectUrl), AdmittedHttpClientRequestFactory.Create(client)), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception signInError)
        {
            error(signInError is McpSignInCancelledException
                ? $"Sign-in to MCP server \"{name}\" was cancelled or not completed within {Math.Round(seconds, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} seconds."
                : $"Sign-in to MCP server \"{name}\" failed: {Message(signInError)}");
            return 1;
        }
        // The original reconnects here and reports the tool count; this command does not connect to the server.
        log($"Signed in to MCP server \"{name}\".");
        return 0;
    }

    private static string Message(Exception error)
    {
        // Host failures wrap the original operation's failure; report the innermost meaningful message.
        var current = error;
        while (current is AggregateException { InnerExceptions.Count: 1 } aggregate) current = aggregate.InnerExceptions[0];
        while (current is McpDefaultOAuthHostFailure && current.InnerException is { } inner) current = inner;
        while (current is AggregateException { InnerExceptions.Count: 1 } nested) current = nested.InnerExceptions[0];
        return current.Message;
    }

    /// <summary>The pasted redirect URL in a terminal; otherwise only the browser callback can finish the sign-in.
    /// Null once <paramref name="signal"/> is cancelled: the callback arrived, or the sign-in timed out.</summary>
    private static async Task<string?> WaitForRedirectUrlAsync(CancellationToken signal, bool interactive)
    {
        if (!interactive)
        {
            try { await Task.Delay(Timeout.Infinite, signal).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return null;
        }
        Console.Error.Write("If the browser cannot reach this machine, paste the URL it was redirected to: ");
        // Console reads cannot be cancelled; an abandoned read ends with the process.
        var read = Task.Run(Console.In.ReadLine);
        try { return await read.WaitAsync(signal).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            using var _ = OperatingSystem.IsWindows() ? Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })
                : Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception) { /* The URL is printed; the user can open it. */ }
    }

    /// <summary>resolve-config-value.ts templates: `$NAME` and `${NAME}` from the environment, `$$` and `$!` escapes.
    /// `!command` values are not run by PiSharp.</summary>
    internal static string ResolveConfigValue(string config, string description, Func<string, string?> environment)
    {
        if (config.StartsWith('!')) throw new InvalidOperationException($"Failed to resolve {description}: PiSharp does not run shell commands for config values.");
        var resolved = new StringBuilder(); var missing = new List<string>(); var index = 0;
        void Env(string variable)
        {
            if (environment(variable) is { Length: > 0 } value) resolved.Append(value);
            else if (!missing.Contains(variable)) missing.Add(variable);
        }
        while (index < config.Length)
        {
            var dollar = config.IndexOf('$', index);
            if (dollar < 0) { resolved.Append(config, index, config.Length - index); break; }
            resolved.Append(config, index, dollar - index);
            var next = dollar + 1 < config.Length ? config[dollar + 1] : '\0';
            if (next is '$' or '!') { resolved.Append(next); index = dollar + 2; continue; }
            if (next == '{')
            {
                var end = config.IndexOf('}', dollar + 2);
                if (end < 0) { resolved.Append('$'); index = dollar + 1; continue; }
                var variable = config[(dollar + 2)..end];
                if (EnvName().IsMatch(variable)) Env(variable); else resolved.Append(config, dollar, end + 1 - dollar);
                index = end + 1; continue;
            }
            var match = EnvPrefix().Match(config, dollar + 1);
            if (match.Success) { Env(match.Value); index = dollar + 1 + match.Length; continue; }
            resolved.Append('$'); index = dollar + 1;
        }
        if (missing.Count == 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variable: {missing[0]}");
        if (missing.Count > 1) throw new InvalidOperationException($"Failed to resolve {description} from environment variables: {string.Join(", ", missing)}");
        return resolved.ToString();
    }

    private static Regex EnvName() => new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static Regex EnvPrefix() => new(@"\G[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
}
