// Authored for PiSharp (no upstream test file): offline fixtures of the `pisharp durable` runtime cases. A temporary pi agent directory with
// a models.json "faux" provider (so the model registry, auth checks and thinking levels are pi's own), a scripted model stream in place of
// the provider (pi-ai's faux provider, reduced; copied here so this suite does not reference the Durable test project), and the host the
// runtime opens with. Nothing reads or writes the real ~/.pi.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

/// <summary>One scripted answer: text (streamed in chunks, optionally slowly or forever), tool calls, or a provider error.</summary>
internal sealed record ScriptedReply
{
    public string? Text { get; init; }
    public ImmutableArray<ToolCallContent> Calls { get; init; }
    public string? Error { get; init; }
    /// <summary>Chunks the text streams in; one chunk when null.</summary>
    public int Chunks { get; init; } = 1;
    /// <summary>The pause before each chunk.</summary>
    public TimeSpan Delay { get; init; }
    /// <summary>Streams "tick " chunks until cancelled.</summary>
    public bool Forever { get; init; }

    public static ScriptedReply Answer(string text) => new() { Text = text };
    public static ScriptedReply Slow(string text, int chunks, TimeSpan delay) => new() { Text = text, Chunks = chunks, Delay = delay };
    public static ScriptedReply Endless(TimeSpan delay) => new() { Forever = true, Delay = delay };
    public static ScriptedReply Failure(string message) => new() { Error = message };
    public static ScriptedReply Call(string name, string argumentsJson, string? id = null) =>
        new() { Calls = [new ToolCallContent(id ?? "call-" + Guid.NewGuid().ToString("N")[..8], name, JsonData.Parse(argumentsJson))] };
}

/// <summary>A request the scripted model received.</summary>
internal sealed record ScriptedRequest(DurableModel Model, ImmutableArray<JsonValue> Messages, DurableStreamOptions? Options)
{
    /// <summary>The last message that is not a system message.</summary>
    public JsonObjectValue? Last => Messages.OfType<JsonObjectValue>().LastOrDefault(message => Role(message) != "system");

    public static string? Role(JsonObjectValue message) => message.TryGetValue("role", out var role) && role is JsonStringValue text ? text.Value : null;

    /// <summary>The text of a message's content.</summary>
    public static string Text(JsonObjectValue? message) => message is not null && message.TryGetValue("content", out var content) ? content switch
    {
        JsonStringValue text => text.Value,
        JsonArrayValue blocks => string.Concat(blocks.OfType<JsonObjectValue>().Select(block =>
            block.TryGetValue("text", out var value) && value is JsonStringValue part ? part.Value : "")),
        _ => "",
    } : "";
}

/// <summary>Model streams that answer each request with the reply the script picks (pi-ai's faux provider, reduced).</summary>
internal sealed class ScriptedStreams(Func<ScriptedRequest, ScriptedReply> respond) : IDurableModels
{
    private readonly ConcurrentQueue<ScriptedRequest> requests = new();

    /// <summary>Answers requests in order; an exhausted script fails the request.</summary>
    public static ScriptedStreams Queue(params ScriptedReply[] replies)
    {
        var queue = new ConcurrentQueue<ScriptedReply>(replies);
        return new(_ => queue.TryDequeue(out var reply) ? reply : ScriptedReply.Failure("No more scripted replies"));
    }

    public IReadOnlyList<ScriptedRequest> Requests => [.. requests];

    /// <summary>Signalled with each request when it arrives.</summary>
    public event Action<ScriptedRequest>? Received;

    public DurableModel? GetModel(string provider, string modelId) => null;

    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var request = new ScriptedRequest(model, messages, options);
        requests.Enqueue(request);
        Received?.Invoke(request);
        var reply = respond(request);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, timestamp, [], TokenUsage.Zero, StopReason.Pending);
        if (reply.Error is { } failure)
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonValue.String(failure).ToJson())),
            });
            yield break;
        }
        yield return new StreamStarted(partial);
        if (!reply.Calls.IsDefaultOrEmpty)
        {
            for (var index = 0; index < reply.Calls.Length; index++)
            {
                var call = reply.Calls[index];
                yield return new ToolCallStarted(index, call with { Arguments = JsonData.Parse("{}") });
                yield return new ToolCallDelta(index, call.Arguments.ToString());
                yield return new ToolCallEnded(index, call);
            }
            yield return new StreamDone(StopReason.ToolUse, partial with { Content = [.. reply.Calls], StopReason = StopReason.ToolUse });
            yield break;
        }
        var text = new StringBuilder();
        yield return new TextStarted(0, new TextContent(""));
        var chunks = reply.Forever ? int.MaxValue : Math.Max(1, reply.Chunks);
        var source = reply.Text ?? "";
        for (var index = 0; index < chunks; index++)
        {
            var aborted = false;
            if (reply.Delay > TimeSpan.Zero)
            {
                try { await Task.Delay(reply.Delay, cancellationToken); }
                catch (OperationCanceledException) { aborted = true; }
            }
            if (aborted || cancellationToken.IsCancellationRequested)
            {
                yield return new StreamError(StopReason.Aborted, partial with
                {
                    Content = [new TextContent(text.ToString())], StopReason = StopReason.Aborted,
                    ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"Request was aborted\"")),
                });
                yield break;
            }
            var chunk = reply.Forever ? "tick " : Chunk(source, index, chunks);
            text.Append(chunk);
            yield return new TextDelta(0, chunk);
        }
        yield return new TextEnded(0, text.ToString());
        yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text.ToString())], StopReason = StopReason.Stop });
    }

    private static string Chunk(string text, int index, int count)
    {
        var size = (int)Math.Ceiling(text.Length / (double)count);
        var start = Math.Min(text.Length, index * size);
        return text.Substring(start, Math.Min(size, text.Length - start));
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent end) terminal = end;
        return terminal!.Message;
    }

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException());

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A temporary pi installation: agent directory, home and project directory.</summary>
internal sealed class TestAgent : IAsyncDisposable
{
    /// <summary>A models.json with the "faux" provider: faux-1 reasons (off..high), faux-2 does not.</summary>
    public const string FauxModels = """
        {"providers":{"faux":{"baseUrl":"http://127.0.0.1:9/v1","apiKey":"faux-test-key","api":"openai-completions","models":[
          {"id":"faux-1","name":"Faux One","reasoning":true,"input":["text"],"contextWindow":128000,"maxTokens":16384,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}},
          {"id":"faux-2","name":"Faux Two","reasoning":false,"input":["text"],"contextWindow":64000,"maxTokens":8192,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}]}}}
        """;

    public TestAgent(string name, string? models = FauxModels, string? settings = """{"defaultProvider":"faux","defaultModel":"faux-1","defaultThinkingLevel":"low"}""")
    {
        Root = Program.Temp("cli-" + name);
        AgentDir = Path.Combine(Root, "agent");
        Home = Path.Combine(Root, "home");
        Cwd = Path.Combine(Root, "project");
        Directory.CreateDirectory(AgentDir);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Cwd);
        if (models is not null) File.WriteAllText(Path.Combine(AgentDir, "models.json"), models);
        if (settings is not null) File.WriteAllText(Path.Combine(AgentDir, "settings.json"), settings);
    }

    public string Root { get; }
    public string AgentDir { get; }
    public string Home { get; }
    public string Cwd { get; }

    /// <summary>An environment with only the given variables.</summary>
    public static Func<string, string?> Env(params (string Name, string Value)[] values) => name =>
    {
        foreach (var (key, value) in values) if (key == name) return value;
        return null;
    };

    /// <summary>The model registry's inputs in this installation.</summary>
    public LiveSessionRuntime Runtime(Func<HttpMessageHandler?>? handler = null, Func<string, string?>? environment = null) =>
        new(environment ?? Env(), handler ?? (() => null), Path.Combine(AgentDir, "auth.json"), ModelsPath: Path.Combine(AgentDir, "models.json"))
        { HomeDirectory = Home };

    /// <summary>The runtime host over this installation with <paramref name="streams"/> in place of the providers.</summary>
    public DurableRuntimeHost Host(IDurableModels? streams, PiLockOptions? lockOptions = null, Func<HttpMessageHandler?>? handler = null) => new()
    {
        AgentDir = AgentDir,
        Home = Home,
        Cwd = Cwd,
        GetEnvironment = Env(),
        LiveRuntime = Runtime(handler),
        ApplicationDirectory = Path.Combine(Root, "app"),
        Streams = streams,
        Lock = lockOptions,
    };

    public async Task<DurableSession> OpenAsync(IDurableModels? streams, bool continueSession = false, PiLockOptions? lockOptions = null) =>
        (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions { ContinueSession = continueSession }, Host(streams, lockOptions));

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}

internal static partial class Program
{
    /// <summary>Waits until <paramref name="condition"/> holds for the view, re-checking on every change.</summary>
    internal static async Task<DurableView> UntilView(IDurableViewSource view, Func<DurableView, bool> condition, string what, int seconds = 20)
    {
        var changed = new SemaphoreSlim(0);
        using var subscription = view.Subscribe(() => changed.Release());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (true)
        {
            var current = view.Current;
            if (condition(current)) return current;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"{what}: not reached; entries {string.Join(",", current.Conversation.Entries.Select(entry => entry.Kind))}; " +
                $"notices {string.Join(" | ", current.Notices.Select(notice => notice.Message))}");
            await changed.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(250, left.TotalMilliseconds)));
        }
    }

    /// <summary>The text of the newest <c>pi.assistant</c> entry of the shown conversation.</summary>
    internal static string? LastAnswer(DurableView view) =>
        view.Conversation.Entries.LastOrDefault(entry => entry.Kind == "pi.assistant") is { Model: { Count: > 0 } messages } && messages[0] is JsonObjectValue message
            ? ScriptedRequest.Text(message) : null;

    /// <summary>Whether the shown conversation runs no turn (its <c>pi.live</c> has no run).</summary>
    internal static bool Idle(DurableView view) => view.Conversation.Doc("pi.live") is not { } live || !live.TryGetValue("run", out var run) || run is JsonNullValue;

    internal static string UserText(EntryRecord entry) => entry.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message ? ScriptedRequest.Text(message) : "";

    /// <summary>Relaunches this test executable as a child (the dotnet host with the entry assembly, or the apphost).</summary>
    internal static ProcessStartInfo ChildStart(IEnumerable<string> arguments)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("No entry assembly"));
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            if (runtime.Parent?.Parent?.Parent is { } root && File.Exists(Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
                start.Environment["DOTNET_ROOT"] = root.FullName;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>A running child whose stdout lines are collected; <see cref="WaitForLineAsync"/> waits for one.</summary>
    internal sealed class Child : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> lines = [];
        private readonly StringBuilder errors = new();
        private readonly SemaphoreSlim arrived = new(0);

        public Child(IEnumerable<string> arguments)
        {
            process = new Process { StartInfo = ChildStart(arguments), EnableRaisingEvents = true };
            process.OutputDataReceived += (_, line) =>
            {
                lock (lines) lines.Add(line.Data ?? "<EOF>");
                arrived.Release();
            };
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (errors) errors.AppendLine(line.Data); };
            if (!process.Start()) throw new InvalidOperationException("The child did not start");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string Transcript()
        {
            lock (lines) lock (errors) return $"\nchild stdout:\n{string.Join("\n", lines)}\nchild stderr:\n{errors}";
        }

        /// <summary>The first stdout line starting with <paramref name="prefix"/>; fails on EOF, an ERROR line or the timeout.</summary>
        public async Task<string> WaitForLineAsync(string prefix, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (lines)
                {
                    if (lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)) is { } found) return found;
                    if (lines.Any(line => line == "<EOF>" || line.StartsWith("ERROR ", StringComparison.Ordinal)))
                        throw new InvalidOperationException($"The child ended before '{prefix}'" + Transcript());
                }
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new TimeoutException($"No '{prefix}' within {timeout.TotalSeconds} s" + Transcript());
                await arrived.WaitAsync(left);
            }
        }

        /// <summary>Kills the child's process tree and waits until it exited.</summary>
        public async Task KillAsync()
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) await KillAsync();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    /// <summary>Writes one protocol line to stdout and flushes it.</summary>
    internal static void Emit(string line)
    {
        lock (Console.Out)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}
