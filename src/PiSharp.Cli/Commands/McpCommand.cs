// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/cli.ts (HELP, parseOptions, parsePairs,
// runMcpCommand, add, remove, list, login, waitForRedirectUrl), packages/coding-agent/src/extensions/mcp/config.ts (addMcpServerConfig,
// removeMcpServerConfig, editMcpServers) and packages/coding-agent/src/core/resolve-config-value.ts (templates).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Commands;

/// <summary>Collaborators of `mcp`; tests replace them with fakes and isolated stores.</summary>
internal sealed record McpCommandOptions(string Cwd, string AgentDirectory)
{
    /// <summary>Defaults to `mcp-auth.json` in the agent directory.</summary>
    public IMcpOAuthCredentialBackend? Credentials { get; init; }
    /// <summary>Defaults to a new socket handler; requests to MCP and authorization servers go through it.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Defaults to the platform browser.</summary>
    public Action<string>? OpenUrl { get; init; }
    /// <summary>The pasted redirect URL. Defaults to a terminal prompt when standard input is a terminal and the browser
    /// is the default one; otherwise only the browser callback finishes the sign-in.</summary>
    public Func<CancellationToken, Task<string?>>? ReadRedirectUrl { get; init; }
    /// <summary>Environment lookup for `${NAME}` in `oauth.clientSecret`.</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
    /// <summary>The environment stdio servers inherit (`mcp list`); defaults to the process environment.</summary>
    public Func<IEnumerable<KeyValuePair<string, string>>> ProcessEnvironment { get; init; } = () =>
        System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(entry => KeyValuePair.Create((string)entry.Key, (string?)entry.Value ?? ""));
    /// <summary>The home directory for `~` in stdio commands.</summary>
    public string HomeDirectory { get; init; } = "";
    /// <summary>A replacement channel for a server (tests); null keeps the stdio or HTTP channel of its config.</summary>
    public Func<McpServerEntry, McpAdmittedChannelFactory?>? CreateChannel { get; init; }
    /// <summary>The stored trust decision for the project (cli.ts: <c>new ProjectTrustStore(agentDir).get(cwd) === true</c>); the
    /// project trust store supplies it. Without a stored decision the project is not trusted.</summary>
    public Func<string, bool> IsProjectTrusted { get; init; } = _ => false;

    internal McpSessionHost CreateSessionHost() => new(AgentDirectory, HomeDirectory, ProcessEnvironment)
    { CreateHttpHandler = HttpHandler is { } handler ? () => handler : null, Credentials = Credentials, CreateChannel = CreateChannel };
}

/// <summary>`mcp`: add, remove and check MCP servers and sign in to them outside a session. Agents run it through bash to
/// configure servers, verify an `mcp.json` they wrote, and start an OAuth sign-in; the user only approves access in the
/// browser. Running sessions pick up new credentials on their next turn.</summary>
internal static class McpCommand
{
    internal const string AppName = "PiSharp.Cli";
    public const string Usage = "mcp add <server> [options] (--url <url> | -- <command> [args...]); mcp remove <server> [-l]; mcp list [--json]; mcp login <server> [--timeout <seconds>]; mcp logout <server>; mcp --help";
    internal const int DefaultLoginTimeoutSeconds = 300;
    internal static readonly string Help = string.Join('\n',
        "Usage:",
        $"  {AppName} mcp add <server> [options] -- <command> [args...]",
        $"  {AppName} mcp add <server> [options] --url <url>",
        $"  {AppName} mcp remove <server> [-l]",
        $"  {AppName} mcp list [--json]",
        $"  {AppName} mcp login <server> [--timeout <seconds>]",
        $"  {AppName} mcp logout <server>",
        "",
        "Configure and check MCP servers and sign in to OAuth servers without starting a session.",
        "Reads ~/.pi/agent/mcp.json and, in trusted projects, .pi/mcp.json.",
        "",
        "Commands:",
        "  add <server>            Add or replace a server in mcp.json",
        "  remove <server>         Remove a server from mcp.json",
        "  list                    Show state, tools, and errors (exits 1 on failure)",
        "  login <server>          Sign in through the browser",
        "  logout <server>         Delete the stored OAuth credentials",
        "",
        "Options for add and remove:",
        "  -l, --local             Use .pi/mcp.json in the current project instead of the global file",
        "",
        "Options for add:",
        "  --url <url>             Streamable HTTP server URL (instead of a command)",
        "  --env <KEY=VALUE>       Environment variable for a stdio server (repeatable)",
        "  --cwd <dir>             Working directory for a stdio server",
        "  --header <KEY=VALUE>    HTTP header (repeatable)",
        "  --bearer-token-env-var <NAME>",
        "                          Send \"Authorization: Bearer ${NAME}\"",
        "  --oauth-client-id <id>  Pre-registered OAuth client id",
        "  --oauth-client-secret <secret>",
        "                          OAuth client secret (may be ${NAME} or !command)",
        "  --oauth-callback-port <port>",
        "                          Fixed OAuth callback port",
        "  --oauth-client-name <name>",
        "                          Client name sent when registering with the OAuth server",
        "  --exposure <mode>       codemode (default), deferred, direct, or hidden",
        "  --description <text>    What the server offers, shown in the system prompt",
        "",
        "Other options:",
        "  --json                  Print the list as JSON",
        $"  --timeout <seconds>     How long login waits for the browser (default: {DefaultLoginTimeoutSeconds})");
    private static readonly string HelpHint = $"Use \"{AppName} mcp --help\" for usage.";

    /// <summary>The agent directory: `PI_CODING_AGENT_DIR`, else `~/.pi/agent`.</summary>
    internal static McpCommandOptions DefaultOptions()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["PI_CODING_AGENT_DIR"] = System.Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") };
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return new(Directory.GetCurrentDirectory(), TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(home, platform, environment))
        { HomeDirectory = home };
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

        var globalConfig = Path.Combine(options.AgentDirectory, "mcp.json");
        var projectConfig = Path.Combine(options.Cwd, ".pi", "mcp.json");
        if (command is "add" or "remove")
            return command == "add" ? Add(args[1..], projectConfig, options, Log, Error) : Remove(args[1..], projectConfig, options, Log, Error);
        if (command is not ("list" or "login" or "logout")) { Error($"Unknown mcp command \"{command}\".\n{HelpHint}"); return 1; }

        McpLoadedConfiguration loaded;
        var projectTrusted = options.IsProjectTrusted(options.Cwd);
        var reading = globalConfig;
        try
        {
            var global = File.Exists(globalConfig) ? new McpConfigurationDocument(globalConfig, File.ReadAllText(globalConfig)) : null;
            reading = projectConfig;
            loaded = McpConfigurationReader.Load(global,
                projectTrusted && File.Exists(projectConfig) ? new(projectConfig, File.ReadAllText(projectConfig)) : null, projectTrusted);
        }
        catch (IOException readError) { Error($"Could not read {reading}: {readError.Message}"); return 1; }
        var untrustedNote = !projectTrusted && File.Exists(projectConfig)
            ? $"{projectConfig} is ignored because the project is not trusted. Start {AppName} in the project to trust it." : null;
        var credentials = new McpOAuthCredentialStore(options.Credentials ?? McpOAuthFileCredentialBackend.InAgentDirectory(options.AgentDirectory));

        if (command == "list")
        {
            var listed = ParseOptions(args[1..], new() { ["json"] = OptionKind.Flag }, Error);
            if (listed is null) return 1;
            if (listed.Positional.Count > 0) { Error($"Usage: {AppName} mcp list [--json]\n{HelpHint}"); return 1; }
            return await ListAsync(loaded, listed.Values.ContainsKey("json"), untrustedNote, options, Log, token).ConfigureAwait(false);
        }
        var parsed = ParseOptions(args[1..], command == "login" ? new() { ["timeout"] = OptionKind.Value } : [], Error);
        if (parsed is null) return 1;
        if (parsed.Positional.Count != 1) { Error($"Usage: {AppName} mcp {command} <server>\n{HelpHint}"); return 1; }
        var name = parsed.Positional[0];
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
        if (command == "logout")
        {
            Log(credentials.Remove(name, url) ? $"Signed out of MCP server \"{name}\"." : $"No stored credentials for MCP server \"{name}\".");
            return 0;
        }
        var timeoutText = parsed.Values.GetValueOrDefault("timeout") ?? DefaultLoginTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        if (!double.TryParse(timeoutText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
        {
            Error("--timeout must be a positive number of seconds.");
            return 1;
        }
        return await LoginAsync(entry, url, seconds, credentials, options, Log, Error, token).ConfigureAwait(false);
    }

    private enum OptionKind { Flag, Value, List }
    private sealed record ParsedOptions(List<string> Positional, Dictionary<string, string?> Values, Dictionary<string, List<string>> Lists);

    /// <summary>`--name value` options; null after reporting an unknown or incomplete one. `--` ends the options, as does reaching
    /// <paramref name="maxPositionals"/> positional arguments, so a command's own options (`add &lt;server&gt; &lt;command&gt; --flag`) pass through.
    /// `-l` is `--local`.</summary>
    private static ParsedOptions? ParseOptions(string[] args, Dictionary<string, OptionKind> known, Action<string> error,
        int maxPositionals = int.MaxValue)
    {
        var parsed = new ParsedOptions([], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index] == "-l" ? "--local" : args[index];
            if (arg == "--" || parsed.Positional.Count >= maxPositionals) { parsed.Positional.AddRange(args[(arg == "--" ? index + 1 : index)..]); break; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { parsed.Positional.Add(arg); continue; }
            var name = arg[2..];
            if (!known.TryGetValue(name, out var kind)) { error($"Unknown option {arg}.\n{HelpHint}"); return null; }
            if (kind == OptionKind.Flag) { parsed.Values[name] = null; continue; }
            if (index + 1 >= args.Length) { error($"{arg} needs a value."); return null; }
            var value = args[++index];
            if (kind == OptionKind.List) { if (!parsed.Lists.TryGetValue(name, out var list)) parsed.Lists[name] = list = []; list.Add(value); }
            else parsed.Values[name] = value;
        }
        return parsed;
    }

    /// <summary>`KEY=VALUE` pairs of a repeatable option; null after reporting a malformed one.</summary>
    private static Dictionary<string, string>? ParsePairs(string option, List<string>? pairs, Action<string> error)
    {
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs ?? [])
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) { error($"--{option} expects KEY=VALUE, got \"{pair}\"."); return null; }
            record[pair[..separator]] = pair[(separator + 1)..];
        }
        return record;
    }

    private static int Add(string[] args, string projectConfig, McpCommandOptions options, Action<string> log, Action<string> error)
    {
        var usage = $"Usage: {AppName} mcp add <server> [options] (--url <url> | -- <command> [args...])\n{HelpHint}";
        var parsed = ParseOptions(args, new()
        {
            ["local"] = OptionKind.Flag, ["url"] = OptionKind.Value, ["env"] = OptionKind.List, ["cwd"] = OptionKind.Value,
            ["header"] = OptionKind.List, ["bearer-token-env-var"] = OptionKind.Value, ["oauth-client-id"] = OptionKind.Value,
            ["oauth-client-secret"] = OptionKind.Value, ["oauth-callback-port"] = OptionKind.Value, ["oauth-client-name"] = OptionKind.Value,
            ["exposure"] = OptionKind.Value, ["description"] = OptionKind.Value
        }, error, 2);
        if (parsed is null) return 1;
        var (positional, values, lists) = parsed;
        var name = positional.FirstOrDefault(); var command = positional.Skip(1).ToList();
        var url = values.GetValueOrDefault("url");
        if (string.IsNullOrEmpty(name) || url is null == (command.Count == 0)) { error(usage); return 1; }
        string? Value(string option) => values.GetValueOrDefault(option);
        string[] httpOnly = ["header", "bearer-token-env-var", "oauth-client-id", "oauth-client-secret", "oauth-callback-port", "oauth-client-name"];
        string[] stdioOnly = ["env", "cwd"];
        var misplaced = (url is null ? httpOnly : stdioOnly).FirstOrDefault(option => values.ContainsKey(option) || lists.ContainsKey(option));
        if (misplaced is not null) { error($"--{misplaced} only applies to {(url is null ? "HTTP servers (--url)" : "stdio servers")}."); return 1; }

        var config = new JsonObject();
        if (url is not null)
        {
            var headers = ParsePairs("header", lists.GetValueOrDefault("header"), error);
            if (headers is null) return 1;
            if (Value("bearer-token-env-var") is { } bearer) headers["Authorization"] = "Bearer ${" + bearer + "}";
            var oauth = new JsonObject();
            if (Value("oauth-client-id") is { } clientId) oauth["clientId"] = clientId;
            if (Value("oauth-client-secret") is { } clientSecret) oauth["clientSecret"] = clientSecret;
            // Number(port): a value that is not a number serializes as null and fails validation.
            if (Value("oauth-callback-port") is { } port)
                oauth["callbackPort"] = double.TryParse(port.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? JsonValue.Create(number) : null;
            if (Value("oauth-client-name") is { } clientName) oauth["clientName"] = clientName;
            config["url"] = url;
            if (headers.Count > 0) config["headers"] = new JsonObject(headers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
            if (oauth.Count > 0) config["oauth"] = oauth;
        }
        else
        {
            var env = ParsePairs("env", lists.GetValueOrDefault("env"), error);
            if (env is null) return 1;
            config["command"] = command[0];
            if (command.Count > 1) config["args"] = new JsonArray([.. command.Skip(1).Select(arg => (JsonNode?)arg)]);
            if (env.Count > 0) config["env"] = new JsonObject(env.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
            if (Value("cwd") is { } cwd) config["cwd"] = cwd;
        }
        if (Value("exposure") is { } exposure) config["exposure"] = exposure;
        if (Value("description") is { } description) config["description"] = description;
        var validated = McpConfigurationReader.Validate(name, JsonSerializer.SerializeToElement(config));
        if (validated.Config is not { } valid) { error(validated.Error!); return 1; }

        var project = values.ContainsKey("local");
        var path = project ? projectConfig : Path.Combine(options.AgentDirectory, "mcp.json");
        var scope = project ? "project" : "global";
        bool replaced = false;
        try
        {
            EditMcpServers(path, (servers, root) =>
            {
                servers ??= [];
                replaced = servers.ContainsKey(name);
                servers[name] = JsonNode.Parse(valid.Raw.ToString());
                root["mcpServers"] = servers;
                return true;
            });
        }
        catch (Exception addError) when (addError is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { error($"Could not update {path}: {addError.Message}"); return 1; }
        log($"{(replaced ? "Replaced" : "Added")} {scope} MCP server \"{name}\" in {path}.");
        if (project && !options.IsProjectTrusted(options.Cwd))
            log($"The project is not trusted, so {path} is ignored until you start {AppName} in the project and trust it.");
        // HTTP servers without an Authorization header may use OAuth.
        var mayNeedSignIn = valid.Transport == McpTransportKind.Http && !(valid.Raw.Value.TryGetProperty("headers", out var written) &&
            written.EnumerateObject().Any(header => header.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)));
        log($"Check it with: {AppName} mcp list{(mayNeedSignIn ? $". If it requires sign-in: {AppName} mcp login {name}" : "")}");
        return 0;
    }

    private static int Remove(string[] args, string projectConfig, McpCommandOptions options, Action<string> log, Action<string> error)
    {
        var parsed = ParseOptions(args, new() { ["local"] = OptionKind.Flag }, error);
        if (parsed is null) return 1;
        if (parsed.Positional.Count != 1) { error($"Usage: {AppName} mcp remove <server> [-l]\n{HelpHint}"); return 1; }
        var name = parsed.Positional[0];
        var project = parsed.Values.ContainsKey("local");
        var globalConfig = Path.Combine(options.AgentDirectory, "mcp.json");
        var path = project ? projectConfig : globalConfig;
        var scope = project ? "project" : "global";
        var removed = false;
        try
        {
            if (File.Exists(path))
                EditMcpServers(path, (servers, _) =>
                {
                    if (servers is null || !servers.ContainsKey(name)) return false;
                    servers.Remove(name); removed = true; return true;
                });
        }
        catch (Exception removeError) when (removeError is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { error($"Could not update {path}: {removeError.Message}"); return 1; }
        if (removed) { log($"Removed {scope} MCP server \"{name}\" from {path}."); return 0; }
        // Where else the server is defined: the other file, read for this hint only.
        McpConfigurationDocument? Read(string file) => File.Exists(file) ? new(file, File.ReadAllText(file)) : null;
        var other = McpConfigurationReader.Load(Read(globalConfig), Read(projectConfig), true).Servers
            .FirstOrDefault(server => server.Name == name && server.Scope != (project ? McpConfigurationScope.Project : McpConfigurationScope.Global));
        error($"No {scope} MCP server named \"{name}\" in {path}." + (other is null ? "" :
            $" It is defined in {other.Source}{(other.Scope == McpConfigurationScope.Project ? "; use --local" : "; omit --local")}."));
        return 1;
    }

    /// <summary>Source editMcpServers: read an `mcp.json` (empty when missing), let <paramref name="edit"/> change its `mcpServers`, and
    /// write it back with its own indentation when the edit returns true. Other content is kept.</summary>
    internal static void EditMcpServers(string path, Func<JsonObject?, JsonObject, bool> edit)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        var parsed = text is null ? new JsonObject() : JsonNode.Parse(text);
        if (parsed is not JsonObject root || root.TryGetPropertyValue("mcpServers", out var servers) && servers is not (null or JsonObject))
            throw new InvalidDataException($"{path}: expected an object with an \"mcpServers\" object");
        if (!edit(servers as JsonObject, root)) return;
        var indent = text is null ? null : Regex.Match(text, @"^([ \t]+)\S", RegexOptions.Multiline) is { Success: true } found ? found.Groups[1].Value : null;
        indent ??= "  ";
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true, IndentCharacter = indent[0], IndentSize = indent.Length, NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Source list: connect to every enabled server, report its state, tools and errors; exit 1 when one is not connected
    /// or the config has errors. Resource counts are not reported.</summary>
    private static async Task<int> ListAsync(McpLoadedConfiguration loaded, bool json, string? untrustedNote, McpCommandOptions options,
        Action<string> log, CancellationToken token)
    {
        var host = options.CreateSessionHost();
        using var client = host.CreateClient();
        var reports = await Task.WhenAll(loaded.Servers.Select(async entry =>
        {
            var report = new JsonObject
            {
                ["name"] = entry.Name, ["scope"] = entry.Scope == McpConfigurationScope.Project ? "project" : "global", ["source"] = entry.Source
            };
            if (entry.Override is { } overridden) report["override"] = overridden;
            var exposure = entry.Config.Exposure.ToString().ToLowerInvariant();
            report["enabled"] = entry.Config.Enabled; report["exposure"] = exposure; report["transport"] = DescribeTransport(entry);
            report["state"] = "disabled"; report["tools"] = new JsonArray();
            if (!entry.Config.Enabled) return report;
            McpServerRuntime? runtime = null;
            try
            {
                var runtimeOptions = new McpRuntimeOptions(1, McpSessionHost.ClientVersion);
                runtime = new McpServerRuntime(entry, runtimeOptions, host.Channel(entry, options.Cwd, runtimeOptions, () => client),
                    (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
                var snapshot = await runtime.ConnectAsync(token).ConfigureAwait(false);
                report["state"] = "connected";
                report["tools"] = new JsonArray([.. snapshot.Catalog.Tools.Select(tool => (JsonNode?)tool.Name)]);
                var overrides = snapshot.Catalog.Tools.Select(tool => (tool.Name, Exposure: McpConfigurationReader.GetToolExposure(entry.Config, tool.Name).ToString().ToLowerInvariant()))
                    .Where(row => row.Exposure != exposure).ToList();
                if (overrides.Count > 0) report["toolExposure"] = new JsonObject(overrides.Select(row => KeyValuePair.Create(row.Name, (JsonNode?)row.Exposure)));
            }
            catch (Exception failure) when (!token.IsCancellationRequested)
            {
                var needsAuth = false;
                for (Exception? current = failure; current is not null; current = current.InnerException)
                    needsAuth |= current is PiSharp.Extensions.Mcp.Authentication.McpOAuthAuthorizationRequiredException;
                report["state"] = needsAuth ? "needs-auth" : "failed";
                report["error"] = needsAuth ? McpProviderTokenAuthentication.SignInRequiredMessage(entry)
                    : failure is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0].Message : failure.Message;
            }
            finally { if (runtime is not null) try { await runtime.CloseAsync().ConfigureAwait(false); } catch (Exception) { } }
            return report;
        })).ConfigureAwait(false);
        var failed = !loaded.Errors.IsEmpty || reports.Any(report => report["enabled"]!.GetValue<bool>() && report["state"]!.GetValue<string>() != "connected");
        if (json)
        {
            var document = new JsonObject { ["servers"] = new JsonArray([.. reports]), ["errors"] = new JsonArray([.. loaded.Errors.Select(text => (JsonNode?)text)]) };
            if (untrustedNote is not null) document["note"] = untrustedNote;
            log(JsonSerializer.Serialize(document, new JsonSerializerOptions
            { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return failed ? 1 : 0;
        }
        if (reports.Length == 0 && loaded.Errors.IsEmpty)
            log($"No MCP servers configured. Add them to {Path.Combine(options.AgentDirectory, "mcp.json")} or .pi/mcp.json.");
        foreach (var report in reports)
        {
            var name = report["name"]!.GetValue<string>(); var state = report["state"]!.GetValue<string>();
            var tools = report["tools"]!.AsArray().Select(tool => tool!.GetValue<string>()).ToList();
            var shown = state == "connected" ? $"connected, {tools.Count} tool{(tools.Count == 1 ? "" : "s")}" : state == "needs-auth" ? "needs sign-in" : state;
            log($"{name}: {shown} ({report["exposure"]!.GetValue<string>()}, {report["scope"]!.GetValue<string>()})");
            log($"  {report["transport"]!.GetValue<string>()}");
            if (report["override"] is { } overridden) log($"  project override: {overridden.GetValue<string>()}");
            if (state == "needs-auth") log($"  sign in with: {AppName} mcp login {name}");
            if (tools.Count > 0)
                log("  tools: " + string.Join(", ", tools.Select(tool => report["toolExposure"]?[tool] is { } toolExposure ? $"{tool} [{toolExposure.GetValue<string>()}]" : tool)));
            if (report["error"] is { } failure) log("  " + failure.GetValue<string>().Replace("\n", "\n  ", StringComparison.Ordinal));
        }
        foreach (var configError in loaded.Errors) log($"config error: {configError}");
        if (untrustedNote is not null) log(untrustedNote);
        return failed ? 1 : 0;
    }

    private static string DescribeTransport(McpServerEntry entry)
    {
        var raw = entry.Config.Raw.Value;
        if (raw.TryGetProperty("url", out var url)) return url.GetString() ?? "";
        var parts = new List<string> { raw.GetProperty("command").GetString() ?? "" };
        if (raw.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array) parts.AddRange(args.EnumerateArray().Select(arg => arg.GetString() ?? ""));
        return string.Join(' ', parts);
    }


    private static async Task<int> LoginAsync(McpServerEntry entry, Uri url, double seconds, McpOAuthCredentialStore credentials,
        McpCommandOptions options, Action<string> log, Action<string> error, CancellationToken token)
    {
        var name = entry.Name;
        var host = options.CreateSessionHost();
        using var connectionClient = host.CreateClient();
        // Connecting first answers whether a sign-in is needed.
        var connected = await ConnectAsync(entry, host, connectionClient, options, token).ConfigureAwait(false);
        if (connected.Tools is { } already) { log($"Already signed in to MCP server \"{name}\" ({already} tools)."); return 0; }
        if (!connected.NeedsSignIn) { error($"MCP server \"{name}\" failed to connect: {connected.Error}"); return 1; }
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
        var reconnected = await ConnectAsync(entry, host, connectionClient, options, token).ConfigureAwait(false);
        if (reconnected.Tools is not { } count)
        {
            error("Signed in, but " + (reconnected.NeedsSignIn ? McpProviderTokenAuthentication.SignInRequiredMessage(entry)
                : $"MCP server \"{name}\" failed to connect: {reconnected.Error}"));
            return 1;
        }
        log($"Signed in to MCP server \"{name}\" ({count} tools).");
        return 0;
    }

    /// <summary>One connection to the server, closed again: its tool count, or whether it needs a sign-in, or its error.</summary>
    private static async Task<(int? Tools, bool NeedsSignIn, string? Error)> ConnectAsync(McpServerEntry entry, McpSessionHost host, HttpClient client,
        McpCommandOptions options, CancellationToken token)
    {
        McpServerRuntime? runtime = null;
        try
        {
            var runtimeOptions = new McpRuntimeOptions(1, McpSessionHost.ClientVersion);
            runtime = new McpServerRuntime(entry, runtimeOptions, host.Channel(entry, options.Cwd, runtimeOptions, () => client),
                (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
            var snapshot = await runtime.ConnectAsync(token).ConfigureAwait(false);
            return (snapshot.Catalog.Tools.Length, false, null);
        }
        catch (Exception failure) when (!token.IsCancellationRequested)
        {
            for (Exception? current = failure; current is not null; current = current.InnerException)
                if (current is PiSharp.Extensions.Mcp.Authentication.McpOAuthAuthorizationRequiredException) return (null, true, null);
            return (null, false, failure is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0].Message : failure.Message);
        }
        finally { if (runtime is not null) try { await runtime.CloseAsync().ConfigureAwait(false); } catch (Exception) { } }
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

    internal static void OpenBrowser(string url)
    {
        try
        {
            using var _ = OperatingSystem.IsWindows() ? Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })
                : Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception) { /* The URL is printed; the user can open it. */ }
    }

    /// <summary>resolve-config-value.ts resolveConfigValueOrThrow: `$NAME` and `${NAME}` from the environment, `$$` and `$!`
    /// escapes, and `!command` run through the shell (owner decision 0004), with the upstream error texts.</summary>
    internal static string ResolveConfigValue(string config, string description, Func<string, string?> environment) =>
        new PiSharp.Cli.Models.ConfigValueResolver(environment).ResolveOrThrow(config, description);
}
