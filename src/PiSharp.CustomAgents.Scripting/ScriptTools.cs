// Decision 0012: C# script tools. ScriptTool compiles a script into a typed tool (arguments as a record, schema from it) or into a tool with a
// JSON schema whose arguments the script reads as a JsonElement; the script returns an AgentToolResult (a string converts). ScriptToolSource
// keeps a folder of *.tool.csx files as a tool source: each file starts with a `// @tool: {...}` header (name, description, parameters,
// replay, deferred) and holds one script. Changes are picked up with a debounced watch: a changed file is compiled again and its tool
// replaced in one install; a file that stops compiling keeps its last good tool and reports the error; a deleted file's tool goes away. A
// replaced script unloads once its running calls finish; a call that starts after that fails with an error result.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.CustomAgents.Plugins;
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Scripting;

/// <summary>What a tool script sees: its arguments, the tool context, and the call's cancellation.</summary>
public sealed class ScriptToolGlobals<TArgs>
{
    /// <summary>The validated arguments.</summary>
    public required TArgs Args { get; init; }
    /// <summary>The tool context (output, idempotency keys, once-only work, services).</summary>
    public required AgentToolContext Context { get; init; }
    /// <summary>Cancelled when the call is.</summary>
    public CancellationToken CancellationToken => Context.CancellationToken;
}

/// <summary>Builds tools from C# scripts.</summary>
public static class ScriptTool
{
    /// <summary>A tool whose arguments are <typeparamref name="TArgs"/> (a public record; its schema is exported), running
    /// <paramref name="code"/>, which reads <c>Args</c> and returns an <see cref="AgentToolResult"/> or a string.</summary>
    public static async Task<ToolRegistration> CreateAsync<TArgs>(string name, string description, string code, CSharpScriptHost? host = null,
        AgentToolOptions? options = null, CancellationToken cancellationToken = default)
    {
        var script = await (host ?? CSharpScriptHost.Shared).CompileAsync<ScriptToolGlobals<TArgs>, AgentToolResult>(code, name + ".csx", cancellationToken)
            .ConfigureAwait(false);
        return AgentTool.Create<TArgs>(name, description, (args, context) => script.RunAsync(new() { Args = args, Context = context }, context.CancellationToken),
            options);
    }

    /// <summary>A tool whose arguments follow <paramref name="parameters"/> (a JSON schema), running <paramref name="code"/>, which reads
    /// <c>Args</c> as a <see cref="JsonElement"/>.</summary>
    public static async Task<ToolRegistration> CreateAsync(string name, string description, string code, JsonObjectValue parameters,
        CSharpScriptHost? host = null, AgentToolOptions? options = null, CancellationToken cancellationToken = default)
    {
        var script = await (host ?? CSharpScriptHost.Shared).CompileAsync<ScriptToolGlobals<JsonElement>, AgentToolResult>(code, name + ".csx", cancellationToken)
            .ConfigureAwait(false);
        return FromScript(name, description, parameters, script, options);
    }

    internal static ToolRegistration FromScript(string name, string description, JsonObjectValue parameters,
        CompiledScript<ScriptToolGlobals<JsonElement>, AgentToolResult> script, AgentToolOptions? options) =>
        AgentTool.Build(name, description, parameters, null, options ?? new AgentToolOptions(),
            (arguments, context) => script.RunAsync(new() { Args = arguments.Clone(), Context = context }, context.CancellationToken));
}

/// <summary>Options of a <see cref="ScriptToolSource"/>.</summary>
public sealed record ScriptToolSourceOptions
{
    /// <summary>Compiles the scripts; default <see cref="CSharpScriptHost.Shared"/>.</summary>
    public CSharpScriptHost? Host { get; init; }
    /// <summary>Whether to reload when files change; default true.</summary>
    public bool Watch { get; init; } = true;
    /// <summary>How long changes settle before a reload; default 300 ms.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>The longest a replaced script waits for its running calls before it unloads; default 30 s.</summary>
    public TimeSpan UnloadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Called with each file that failed to load and each failed reload.</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>A failed script file: why it did not load (its last good tool, if any, stays).</summary>
public sealed class ScriptToolLoadException(string file, string message, Exception? inner = null) : Exception($"{file}: {message}", inner)
{
    /// <summary>The file.</summary>
    public string File { get; } = file;
}

/// <summary>A folder of <c>*.tool.csx</c> scripts as a tool source, reloaded when the files change.</summary>
public sealed class ScriptToolSource : ToolSource, IAsyncDisposable
{
    /// <summary>The files a source loads.</summary>
    public const string FilePattern = "*.tool.csx";
    /// <summary>The first line of every file: <c>// @tool: { "name"?, "description", "parameters"?, "replay"?, "deferred"? }</c>.</summary>
    public const string HeaderPrefix = "// @tool:";

    private readonly ScriptToolSourceOptions options;
    private readonly CSharpScriptHost host;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Task> retiring = [];
    private ImmutableDictionary<string, Loaded> loaded = ImmutableDictionary<string, Loaded>.Empty;
    private ImmutableDictionary<string, string> errors = ImmutableDictionary<string, string>.Empty;
    private FolderWatch? watch;
    private bool disposed;

    /// <summary>A source named <paramref name="id"/> over the scripts in <paramref name="directory"/>; call <see cref="LoadAsync"/> to load them.</summary>
    public ScriptToolSource(string id, string directory, ScriptToolSourceOptions? options = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        this.options = options ?? new ScriptToolSourceOptions();
        host = this.options.Host ?? CSharpScriptHost.Shared;
    }

    /// <summary>The folder.</summary>
    public string Directory { get; }
    /// <summary>Files that failed to load at the last reload, with why.</summary>
    public IReadOnlyDictionary<string, string> Errors => errors;

    /// <summary>Loads the folder's scripts, then follows changes when <see cref="ScriptToolSourceOptions.Watch"/> is set.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (options.Watch && watch is null && !disposed)
            watch = new FolderWatch(Directory, options.Debounce, () => ReloadAsync(CancellationToken.None), options.OnError, FilePattern, deletes: true);
    }

    /// <summary>Compiles what changed and publishes the folder's tools in one install.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = ImmutableDictionary.CreateBuilder<string, Loaded>(StringComparer.OrdinalIgnoreCase);
            var failed = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            var created = new List<Loaded>();
            string[] files = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, FilePattern) : [];
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                loaded.TryGetValue(file, out var previous);
                try
                {
                    var text = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
                    var hash = CSharpScriptHost.HashOf(text);
                    if (previous is not null && previous.Hash == hash)
                    {
                        next[file] = previous;
                        continue;
                    }
                    var fresh = Load(file, text, hash);
                    created.Add(fresh);
                    next[file] = fresh;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    var failure = error as ScriptToolLoadException ?? new ScriptToolLoadException(file, error.Message, error);
                    failed[file] = failure.Message;
                    options.OnError?.Invoke(failure);
                    if (previous is not null) next[file] = previous;
                }
            }
            var kept = next.ToImmutable();
            try
            {
                Publish(_ => [.. kept.Values.OrderBy(entry => entry.File, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Tool)]);
            }
            catch
            {
                foreach (var entry in created) Retire(entry);
                throw;
            }
            foreach (var entry in loaded.Values.Where(entry => !kept.Values.Contains(entry))) Retire(entry);
            loaded = kept;
            errors = failed.ToImmutable();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Resolves when every replaced script has unloaded (or timed out waiting for its calls).</summary>
    public Task WhenRetiredAsync()
    {
        lock (retiring) return Task.WhenAll([.. retiring]);
    }

    /// <summary>The load context of the script behind <paramref name="tool"/>, weakly; null when there is no such tool.</summary>
    internal WeakReference? ContextOf(string tool) => loaded.Values.FirstOrDefault(entry => entry.Tool.Name == tool)?.Script.Context;

    /// <summary>Stops watching and unloads every script.</summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            watch?.Dispose();
            foreach (var entry in loaded.Values) Retire(entry);
            loaded = ImmutableDictionary<string, Loaded>.Empty;
        }
        finally
        {
            gate.Release();
        }
        await WhenRetiredAsync().ConfigureAwait(false);
    }

    private Loaded Load(string file, string text, string hash)
    {
        var header = Header(file, text);
        var script = host.Compile<ScriptToolGlobals<JsonElement>, AgentToolResult>(text, file);
        try
        {
            var tool = ScriptTool.FromScript(header.Name, header.Description, header.Parameters, script, new AgentToolOptions
            {
                Replay = header.Replay,
                Deferred = header.Deferred,
            });
            return new Loaded(file, hash, tool, script);
        }
        catch
        {
            _ = script.UnloadAsync();
            throw;
        }
    }

    private void Retire(Loaded entry)
    {
        var unloading = entry.Script.UnloadAsync(options.UnloadTimeout);
        lock (retiring)
        {
            retiring.RemoveAll(task => task.IsCompleted);
            retiring.Add(unloading);
        }
    }

    /// <summary>Reads a file an editor may still be writing.</summary>
    private static async Task<string> ReadAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false); }
            catch (IOException) when (attempt < 5) { await Task.Delay(50 * attempt, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static ScriptHeader Header(string file, string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var first = (newline < 0 ? text : text[..newline]).TrimEnd('\r').Trim();
        if (!first.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            throw new ScriptToolLoadException(file, $"The first line must be a header: {HeaderPrefix} {{ \"description\": \"...\" }}.");
        JsonObjectValue fields;
        try { fields = JsonValue.Parse(first[HeaderPrefix.Length..]) as JsonObjectValue ?? throw new FormatException("The header is not a JSON object."); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new ScriptToolLoadException(file, "The header is not valid JSON: " + error.Message, error); }
        string? Text(string key) => fields.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;
        var name = Text("name") ?? Path.GetFileName(file)[..^".tool.csx".Length];
        var description = Text("description") ?? throw new ScriptToolLoadException(file, "The header has no description.");
        var parameters = fields.TryGetValue("parameters", out var schema)
            ? schema as JsonObjectValue ?? throw new ScriptToolLoadException(file, "The header's parameters are not a JSON schema object.")
            : ToolRegistration.EmptyParameters;
        var replay = Text("replay") switch
        {
            null or "unsafe" => ToolReplay.Unsafe,
            "safe" => ToolReplay.Safe,
            var other => throw new ScriptToolLoadException(file, $"The header's replay is '{other}'; use \"safe\" or \"unsafe\"."),
        };
        var deferred = fields.TryGetValue("deferred", out var flag) && flag is JsonBooleanValue { Value: true };
        return new ScriptHeader(name, description, parameters, replay, deferred);
    }

    private sealed record ScriptHeader(string Name, string Description, JsonObjectValue Parameters, ToolReplay Replay, bool Deferred);

    private sealed record Loaded(string File, string Hash, ToolRegistration Tool, CompiledScript<ScriptToolGlobals<JsonElement>, AgentToolResult> Script);
}
