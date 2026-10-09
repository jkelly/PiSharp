// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (loadExtensions,
// discoverAndLoadExtensions), packages/coding-agent/src/core/extensions/runner.ts (ExtensionRunner: getFlags, getShortcuts,
// getMessageRenderer, getEntryRenderer, getMarkdownTransformers, getRegisteredCommands, emitError) and
// packages/coding-agent/src/core/agent-session-services.ts (applyExtensionFlagValues).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node.Pi;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>One loaded extension: its index in the Node host, its path as given and resolved, and what it registered.</summary>
internal sealed record PiLoadedExtension(int Index, string Path, string ResolvedPath)
{
    internal JsonObject Descriptor { get; set; } = new();
    /// <summary>The registry owner id of this extension (registry identifiers are ASCII; the path is kept in <see cref="Path"/>).</summary>
    internal string OwnerId => "pi-extension-" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal IEnumerable<string> Events => (Descriptor["events"] as JsonArray ?? []).Select(node => node!.GetValue<string>());
    internal int HandlerCount(string eventName) => Descriptor["handlerCounts"] is JsonObject counts && counts[eventName] is JsonValue value ? value.GetValue<int>() : 1;
}

/// <summary>Source ExtensionError of a load: the path and the error text.</summary>
internal sealed record PiExtensionLoadError(string Path, string Error);

/// <summary>What a Pi-style run needs to start the extension host.</summary>
internal sealed record PiExtensionHostOptions(string Cwd, string AgentDir, string Mode, bool HasUI)
{
    internal string? NodeExecutable { get; init; }
    internal string? HostScript { get; init; }
    internal Func<string, string?> GetEnvironment { get; init; } = Environment.GetEnvironmentVariable;
    internal Action<string>? StandardError { get; init; }
    internal string? Theme { get; init; }
    /// <summary>The Pi packages' node_modules to load extensions with; null installs or finds them (<see cref="PiNodeRuntime"/>).</summary>
    internal string? PiModules { get; init; }
    internal ImmutableArray<PiTheme> Themes { get; init; } = [];
    internal Func<string, bool> ProjectTrusted { get; init; } = _ => true;
}

/// <summary>
/// The extensions of one Pi-style run, loaded by the Node extension host (TypeScript and JavaScript run in the user's Node, as upstream
/// runs them in its own process). Before the session starts it answers the CLI's own events (project_trust, resources_discover) and
/// the flags; <see cref="NativeExtensionActivation.LoadPiAsync"/> then binds every extension to the session as a registry owner whose
/// callbacks call into Node. Upstream semantics that need the session (actions, context reads, UI) are served here.
/// </summary>
internal sealed partial class PiExtensionHost : IPiNodeHostPeer, IAsyncDisposable
{
    internal static ExtensionRegistryOptions RegistryOptions { get; } = new()
    {
        MaximumOwners = 1024, MaximumRegistrations = 16_384, MaximumRegistrationsPerOwner = 2_048, MaximumMetadataCharacters = 64 * 1024 * 1024,
        MaximumDescriptionCharacters = 1024 * 1024, MaximumJsonCharacters = 64 * 1024 * 1024, MaximumJsonDepth = 64,
        // Pi extensions may replace built-in tools (registerTool with a built-in name) and register any command name.
        ReservedToolNames = [], ReservedCommandNames = []
    };

    private readonly PiExtensionHostOptions _options;
    private PiNodeHost? _node;
    private readonly List<PiLoadedExtension> _extensions = [];
    private readonly List<PiExtensionLoadError> _errors = [];
    private readonly Dictionary<string, JsonNode?> _flagValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, IExtensionContext> _contexts = new();
    private readonly List<JsonObject> _providers = [];
    private readonly List<JsonObject> _virtualModels = [];
    private readonly List<JsonObject> _mcpServers = [];
    private long _nextContext;
    private IExtensionContext? _lastContext;

    private PiExtensionHost(PiExtensionHostOptions options) { _options = options; }

    internal string Cwd => _options.Cwd;
    internal string Mode => _options.Mode;
    internal bool HasUI => _options.HasUI;
    internal string BridgeDirectory { get; private set; } = AppContext.BaseDirectory;
    /// <summary>The installed Pi packages' node_modules the extensions run against, or null with <see cref="RuntimeFallback"/>.</summary>
    internal string? PiModules { get; private set; }
    /// <summary>Why the run uses PiSharp's compatibility modules instead of the installed Pi packages (offline, no npm, failed install).</summary>
    internal string? RuntimeFallback { get; private set; }
    /// <summary>What the Node host loaded: <c>pi@1.1.0</c> or <c>compatibility</c>.</summary>
    internal string Modules { get; private set; } = "compatibility";
    internal ImmutableArray<PiLoadedExtension> Extensions { get { lock (_extensions) return [.. _extensions]; } }
    internal ImmutableArray<PiExtensionLoadError> Errors { get { lock (_extensions) return [.. _errors]; } }
    internal bool IsRunning => _node is { HasExited: false };
    /// <summary>Provider, virtual model and MCP server registrations reported by the extensions (in order).</summary>
    internal ImmutableArray<JsonObject> ProviderRegistrations { get { lock (_providers) return [.. _providers]; } }
    internal ImmutableArray<JsonObject> VirtualModelRegistrations { get { lock (_virtualModels) return [.. _virtualModels]; } }
    internal ImmutableArray<JsonObject> McpServerRegistrations { get { lock (_mcpServers) return [.. _mcpServers]; } }

    /// <summary>Source emitError listeners: an extension's handler error (<c>extensionPath</c>, <c>event</c>, <c>error</c>). The session
    /// host reports it as the mode does (print: <c>Extension error (path): error</c> on stderr; RPC: an <c>extension_error</c> record).</summary>
    internal Func<string, string, string, ValueTask>? ReportError { get; set; }
    /// <summary>Called when a running extension registers something new (tool, command, handler...); IMPL-I refreshes its views.</summary>
    internal Action? RegistrationsChanged { get; set; }

    /// <summary>Starts the Node host. Throws <see cref="PiExtensionHostUnavailableException"/> when Node or the host script is missing.</summary>
    internal static async Task<PiExtensionHost> StartAsync(PiExtensionHostOptions options, CancellationToken token)
    {
        var host = new PiExtensionHost(options);
        var node = options.NodeExecutable ?? PiNodeHost.FindNode(options.GetEnvironment)
            ?? throw new PiExtensionHostUnavailableException("TypeScript and JavaScript extensions need Node.js on PATH (or PISHARP_NODE).");
        var script = options.HostScript ?? PiNodeHost.FindHostScript(options.GetEnvironment)
            ?? throw new PiExtensionHostUnavailableException("The PiSharp Node extension host (node-bridge/pi-host/host.mjs) is missing from this installation.");
        host.BridgeDirectory = Path.GetDirectoryName(Path.GetDirectoryName(script)!)!;
        var cwd = Directory.Exists(options.Cwd) ? options.Cwd : Environment.CurrentDirectory;
        // The Pi 1.1.0 packages extensions run against (installed into the agent dir on first use; the compatibility modules offline).
        var installOutput = new PiNodeRuntimeOutput(options.StandardError);
        (host.PiModules, host.RuntimeFallback) = options.PiModules is { } configured ? (configured, null)
            : await PiNodeRuntime.EnsureAsync(options.AgentDir, options.GetEnvironment, installOutput, null, null, token).ConfigureAwait(false);
        host._node = await PiNodeHost.StartAsync(new PiNodeHostLaunch(node, script, cwd) { StandardError = options.StandardError }, host, token).ConfigureAwait(false);
        var init = await host._node.RequestAsync("init", new JsonObject
        {
            ["cwd"] = options.Cwd, ["agentDir"] = options.AgentDir, ["mode"] = options.Mode, ["hasUI"] = options.HasUI,
            ["theme"] = options.Theme ?? "dark", ["version"] = PiConfig.Version, ["flagValues"] = new JsonObject(),
            ["piModules"] = host.PiModules
        }, token).ConfigureAwait(false);
        host.Modules = init is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("modules", out var modules) && modules.ValueKind == JsonValueKind.String
            ? modules.GetString()! : "compatibility";
        return host;
    }

    private PiNodeHost Node => _node ?? throw new InvalidOperationException("The Node extension host is not running.");

    /// <summary>Source loadExtensions over the paths, in order: each module is imported and its factory awaited. A failure is recorded
    /// as <c>Failed to load extension: …</c> (or the factory error) and the next path loads.</summary>
    internal async Task LoadAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (paths.Count == 0) return;
        var result = await Node.RequestAsync("load", new JsonObject { ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode)path)]) }, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Node extension host returned no load result.");
        lock (_extensions)
        {
            foreach (var item in result.GetProperty("results").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                if (item.TryGetProperty("error", out var error)) { _errors.Add(new(path, error.GetString() ?? "Failed to load extension")); continue; }
                var descriptor = JsonNode.Parse(item.GetProperty("extension").GetRawText())!.AsObject();
                _extensions.Add(new(descriptor["index"]!.GetValue<int>(), path, descriptor["resolvedPath"]!.GetValue<string>()) { Descriptor = descriptor });
            }
            foreach (var flag in result.GetProperty("flagValues").EnumerateObject()) _flagValues[flag.Name] = JsonNode.Parse(flag.Value.GetRawText());
        }
    }

    /// <summary>Orders the loaded extensions as the final extension paths list them (loadFinalExtensionSet); unlisted ones keep their place
    /// at the end.</summary>
    internal void Reorder(IEnumerable<string> paths)
    {
        var order = paths.Select(Path.GetFullPath).ToList();
        lock (_extensions)
        {
            var ranked = _extensions.Select((extension, position) => (extension, rank: order.FindIndex(path => PiPaths.Comparer.Equals(path, extension.ResolvedPath)), position))
                .OrderBy(item => item.rank < 0 ? int.MaxValue : item.rank).ThenBy(item => item.position).Select(item => item.extension).ToList();
            _extensions.Clear(); _extensions.AddRange(ranked);
        }
    }

    /// <summary>Source getFlags: every extension flag, first registration of a name wins.</summary>
    internal ImmutableArray<PiExtensionFlag> Flags
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var flags = ImmutableArray.CreateBuilder<PiExtensionFlag>();
            foreach (var extension in Extensions)
                foreach (var flag in (extension.Descriptor["flags"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = flag["name"]!.GetValue<string>();
                    if (seen.Add(name)) flags.Add(new(name, flag["type"]?.GetValue<string>() ?? "boolean", flag["description"]?.GetValue<string>(), extension.Path));
                }
            return flags.ToImmutable();
        }
    }

    /// <summary>Source applyExtensionFlagValues: a registered boolean flag becomes true, a string flag takes its value (missing value is
    /// an error); unregistered names are reported together as <c>Unknown option(s): --a, --b</c>.</summary>
    internal async Task<ImmutableArray<PiDiagnostic>> ApplyFlagValuesAsync(IReadOnlyDictionary<string, string?> values, CancellationToken token)
    {
        var diagnostics = ImmutableArray.CreateBuilder<PiDiagnostic>();
        if (values.Count == 0) return [];
        var registered = Flags.ToDictionary(flag => flag.Name, flag => flag.Type, StringComparer.Ordinal);
        var unknown = new List<string>(); var applied = new JsonObject();
        foreach (var (name, value) in values)
        {
            if (!registered.TryGetValue(name, out var type)) { unknown.Add(name); continue; }
            if (type == "boolean") { applied[name] = true; continue; }
            if (value is not null) { applied[name] = value; continue; }
            diagnostics.Add(new("error", $"Extension flag \"--{name}\" requires a value"));
        }
        if (unknown.Count > 0) diagnostics.Add(new("error", $"Unknown option{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown.Select(name => "--" + name))}"));
        if (applied.Count > 0)
        {
            lock (_extensions) foreach (var (name, value) in applied) _flagValues[name] = value?.DeepClone();
            if (IsRunning) await Node.RequestAsync("flags.set", new JsonObject { ["values"] = applied }, token).ConfigureAwait(false);
        }
        return diagnostics.ToImmutable();
    }

    internal JsonNode? FlagValue(string name) { lock (_extensions) return _flagValues.TryGetValue(name, out var value) ? value?.DeepClone() : null; }

    /// <summary>The extension path of a registry owner id (MCP servers and diagnostics report paths).</summary>
    internal string PathOfOwner(string ownerId) => Extensions.FirstOrDefault(extension => extension.OwnerId == ownerId)?.Path ?? ownerId;

    /// <summary>Registers every loaded extension as a registry owner, in load order.</summary>
    internal async Task ActivateAsync(ExtensionRegistry registry, CancellationToken token)
    {
        var names = CommandInvocationNames();
        foreach (var extension in Extensions)
            await registry.ActivateAsync(extension.OwnerId, new PiNodeOwner(this, extension, names), token).ConfigureAwait(false);
    }

    /// <summary>Source resolveRegisteredCommands: a command name registered more than once (across extensions, in load order) is
    /// invoked as <c>name:1</c>, <c>name:2</c>…, skipping taken names.</summary>
    internal Dictionary<(int Extension, string Name), string> CommandInvocationNames()
    {
        var commands = Extensions.SelectMany(extension => (extension.Descriptor["commands"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(command => (extension.Index, Name: command["name"]!.GetValue<string>()))).ToList();
        var counts = commands.GroupBy(command => command.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal); var taken = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<(int, string), string>();
        foreach (var (index, name) in commands)
        {
            var occurrence = seen[name] = seen.GetValueOrDefault(name) + 1;
            var invocation = counts[name] > 1 ? $"{name}:{occurrence}" : name;
            if (taken.Contains(invocation))
            {
                var suffix = occurrence;
                do { suffix++; invocation = $"{name}:{suffix}"; } while (taken.Contains(invocation));
            }
            taken.Add(invocation);
            names[(index, name)] = invocation;
        }
        return names;
    }

    /// <summary>resource-loader.ts detectExtensionConflicts: a tool or flag name an earlier extension registered is an error of the later one.</summary>
    internal ImmutableArray<PiExtensionLoadError> Conflicts()
    {
        var conflicts = ImmutableArray.CreateBuilder<PiExtensionLoadError>();
        var tools = new Dictionary<string, string>(StringComparer.Ordinal); var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in Extensions)
        {
            foreach (var name in (extension.Descriptor["tools"] as JsonArray ?? []).OfType<JsonObject>().Select(tool => tool["name"]!.GetValue<string>()))
                if (tools.TryGetValue(name, out var owner) && owner != extension.Path) conflicts.Add(new(extension.Path, $"Tool \"{name}\" conflicts with {owner}"));
                else tools[name] = extension.Path;
            foreach (var name in (extension.Descriptor["flags"] as JsonArray ?? []).OfType<JsonObject>().Select(flag => flag["name"]!.GetValue<string>()))
                if (flags.TryGetValue(name, out var owner) && owner != extension.Path) conflicts.Add(new(extension.Path, $"Flag \"--{name}\" conflicts with {owner}"));
                else flags[name] = extension.Path;
        }
        return conflicts.ToImmutable();
    }

    /// <summary>A registry over the loaded extensions for the CLI's own events before the session exists (project_trust,
    /// resources_discover), as upstream emits them over the loaded extensions result.</summary>
    internal async Task<PiLoadedExtensions> PreSessionAsync(CancellationToken token)
    {
        var registry = new ExtensionRegistry(RegistryOptions);
        await ActivateAsync(registry, token).ConfigureAwait(false);
        return new(registry, registry.CaptureSnapshot()) { Host = this };
    }

    // ----------------------------------------------------------------------------------------------------------------- contexts

    /// <summary>Registers the native context of a callback for the extension's synchronous reads and dialogs; disposal releases it.</summary>
    internal ContextLease Enter(IExtensionContext context)
    {
        var id = Interlocked.Increment(ref _nextContext);
        _contexts[id] = context; Volatile.Write(ref _lastContext, context);
        return new(this, id);
    }
    internal readonly struct ContextLease(PiExtensionHost host, long id) : IDisposable
    {
        internal long Id => id;
        public void Dispose() => host._contexts.TryRemove(id, out _);
    }
    private IExtensionContext? ContextOf(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("ctx", out var id) && id.ValueKind == JsonValueKind.Number &&
        _contexts.TryGetValue(id.GetInt64(), out var context) ? context : _contexts.Values.LastOrDefault() ?? Volatile.Read(ref _lastContext);

    /// <summary>Sends a request to the Node host on behalf of a native callback.</summary>
    internal Task<JsonElement?> CallAsync(string method, JsonObject parameters, CancellationToken token, Action<JsonElement>? onProgress = null,
        bool afterPrecedingFrames = false) => Node.RequestAsync(method, parameters, token, onProgress, afterPrecedingFrames);

    /// <summary>Reports extension errors returned by a Node dispatch (<c>errors: [{ event, error }]</c>).</summary>
    internal async ValueTask ReportErrorsAsync(PiLoadedExtension extension, JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return;
        foreach (var error in errors.EnumerateArray())
            await ReportAsync(extension.Path, error.GetProperty("event").GetString() ?? "", error.GetProperty("error").GetString() ?? "").ConfigureAwait(false);
    }
    internal ValueTask ReportAsync(string path, string eventName, string error) => ReportError?.Invoke(path, eventName, error) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_node is { } node) await node.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Node.js or the bridge is not available, so TypeScript/JavaScript extensions cannot load.</summary>
internal sealed class PiExtensionHostUnavailableException(string message) : Exception(message);
