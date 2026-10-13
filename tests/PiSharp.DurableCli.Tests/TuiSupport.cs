// Support for the tui.* cases: a fake view source and controller over hand-built DurableViews, and the TUI running on a VirtualTerminal
// (tests/PiSharp.InteractiveParity.Tests/VirtualTerminal.cs, linked). PiSharp.Durable builds ConversationView and TaskGraph values only
// internally (they come from a Harness's mounts), so the fake builds them through their non-public constructors with the JSON shapes the
// Harness writes: entries carry pi-ai wire messages in `model`, and the built-in documents are pi.agent, pi.live, pi.inbox and pi.usage.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Tui.Pi;

internal static partial class TuiTests
{
    public const string Cwd = "/work/project";
    public static readonly ConversationId RootId = ConversationId.Root;

    public static JsonObjectValue J(string json) => (JsonObjectValue)JsonValue.Parse(json);

    public static string Usage(int input = 0, int output = 0, int cacheRead = 0, int cacheWrite = 0, double cost = 0) =>
        $$$"""{"input":{{{input}}},"output":{{{output}}},"cacheRead":{{{cacheRead}}},"cacheWrite":{{{cacheWrite}}},"totalTokens":{{{input + output + cacheRead + cacheWrite}}},"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":{{{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}""";

    public static string UserWire(string text) => $$"""{"role":"user","content":{{Quote(text)}},"timestamp":1}""";

    public static string AssistantWire(string contentJson, string stopReason = "stop", string? usage = null) =>
        $$"""{"role":"assistant","content":{{contentJson}},"api":"faux","provider":"faux","model":"faux-1","usage":{{usage ?? Usage()}},"stopReason":"{{stopReason}}","timestamp":1}""";

    public static string TextBlock(string text) => $$"""{"type":"text","text":{{Quote(text)}}}""";

    public static string ToolCallBlock(string id, string name, string argumentsJson) =>
        $$"""{"type":"toolCall","id":"{{id}}","name":"{{name}}","arguments":{{argumentsJson}}}""";

    public static string ToolResultWire(string id, string name, string text, bool isError = false) =>
        $$"""{"role":"toolResult","toolCallId":"{{id}}","toolName":"{{name}}","content":[{{TextBlock(text)}}],"isError":{{(isError ? "true" : "false")}},"timestamp":1}""";

    public static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    public static EntryRecord Entry(long id, string kind, string? wire = null, ConversationId? conversation = null) => new()
    {
        Id = new EntryId(id),
        ConversationId = conversation ?? RootId,
        Kind = kind,
        Model = wire is null ? null : JsonValue.Array(JsonValue.Parse(wire)),
    };

    public static EntryRecord UserEntry(long id, string text) => Entry(id, "pi.user", UserWire(text));
    public static EntryRecord AssistantEntry(long id, string text, string? usage = null) =>
        Entry(id, "pi.assistant", AssistantWire($"[{TextBlock(text)}]", usage: usage));

    /// <summary>A ConversationView with <paramref name="entries"/> and the documents of <paramref name="docsJson"/>.</summary>
    public static ConversationView Conversation(IEnumerable<EntryRecord> entries, string docsJson = "{}", ConversationId? id = null) =>
        (ConversationView)Activator.CreateInstance(typeof(ConversationView), BindingFlags.NonPublic | BindingFlags.Instance, null,
            [new ConversationRecord(id ?? RootId), entries.ToImmutableArray(), J(docsJson)], null)!;

    public const string AgentDoc = """{"model":{"provider":"faux","modelId":"faux-1"},"thinkingLevel":"medium"}""";

    public static readonly ImmutableArray<DurableModelSummary> Models =
    [
        new("anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", 200_000),
        new("faux", "faux-1", "Faux 1", 100_000),
        new("openai", "gpt-5", "GPT-5", 400_000),
    ];

    public static readonly ImmutableArray<DurableConversationSummary> Conversations =
        [new(RootId, "main"), new(new ConversationId(2), "subagent 2", "Summarize the README")];

    public static DurableView View(ConversationView conversation, IEnumerable<DurableNotice>? notices = null, TaskGraph? tasks = null,
        ImmutableArray<DurableConversationSummary>? conversations = null) =>
        new(new DurableSessionInfo("session-1", "/sessions/session-1", Cwd), conversation, conversations ?? Conversations, Models,
            [.. notices ?? []], tasks);

    public static DurableView View(IEnumerable<EntryRecord> entries, string? live = null, string? inbox = null, string? usage = null,
        string agent = AgentDoc, IEnumerable<DurableNotice>? notices = null, TaskGraph? tasks = null)
    {
        var docs = new List<string> { $"\"pi.agent\":{agent}" };
        if (live is not null) docs.Add($"\"pi.live\":{live}");
        if (inbox is not null) docs.Add($"\"pi.inbox\":{inbox}");
        if (usage is not null) docs.Add($"\"pi.usage\":{usage}");
        return View(Conversation(entries, "{" + string.Join(",", docs) + "}"), notices, tasks);
    }

    // ─── task graphs ─────────────────────────────────────────────────────────

    public static TaskGraphState State(DurableTaskStatus status, string? phase, ImmutableArray<TaskId>? on = null, JoinPolicy? policy = null,
        TaskOutcomeStatus? outcome = null) =>
        (TaskGraphState)typeof(TaskGraphState).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([status, phase, on ?? [], policy, outcome]);

    public static TaskGraphNode Node(long id, string kind, ConversationId conversation, TaskGraphState state, long? owner = null,
        bool background = false, bool abortRequested = false, long[]? conversations = null) =>
        (TaskGraphNode)typeof(TaskGraphNode).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([new TaskId(id), kind, conversation, owner is null ? null : new TaskId(owner.Value), background, abortRequested, state,
                (conversations ?? []).Select(value => new ConversationId(value)).ToImmutableArray()]);

    public static TaskGraph Graph(params TaskGraphNode[] nodes) =>
        (TaskGraph)typeof(TaskGraph).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([nodes.ToImmutableSortedDictionary(node => node.Id, node => node,
                Comparer<TaskId>.Create((left, right) => left.Value.CompareTo(right.Value)))]);

    // ─── text ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\x1b\[[0-9;:?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b_[^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex AnsiPattern();

    public static string Plain(string text) => AnsiPattern().Replace(text, "");

    /// <summary>The component's lines at <paramref name="width"/> without styling or trailing spaces, as the screen shows them.</summary>
    public static List<string> PlainLines(IComponent component, int width) =>
        [.. component.Render(width).Select(line => Plain(line).TrimEnd())];

    public static int Count(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The screen's lines trimmed and joined by single spaces, so text the footer wraps reads as one line.</summary>
    public static string Flat(string screen) =>
        string.Join(" ", screen.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    /// <summary>Asserts that the non-empty <paramref name="expected"/> lines appear on <paramref name="screen"/> in order, each as a whole
    /// trimmed screen line.</summary>
    public static void ShowsInOrder(string screen, IEnumerable<string> expected, string what)
    {
        var lines = screen.Split('\n').Select(line => line.Trim()).ToList();
        var at = 0;
        foreach (var line in expected.Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            var found = lines.FindIndex(at, candidate => candidate == line);
            if (found < 0) throw new InvalidOperationException($"{what}: the screen lacks \"{line}\" after line {at}. Screen:\n{screen}");
            at = found + 1;
        }
    }

    /// <summary>A TUI for rendering a component on its own, as pi's interactive mode would draw it; never started.</summary>
    public static ITui StandaloneTui() => new TuiMainScreen(new ProcessTerminal(UiLoop.CreateManual(), new VirtualTerminal(), _ => null),
        UiLoop.CreateManual());

    /// <summary>The follow-up key as the terminal sends it: Ctrl+Q with Windows keybindings, Alt+Enter otherwise.</summary>
    public static string FollowUpKey => AppKeybindings.UseWindowsKeybindings() ? "\u0011" : "\u001b\r";
}

/// <summary>A view source whose view the test replaces; listeners run on the publishing thread, as the runtime's do.</summary>
internal sealed class FakeViewSource(DurableView initial) : IDurableViewSource
{
    private readonly Lock gate = new();
    private readonly List<Action> listeners = [];
    private DurableView current = initial;

    public DurableView Current { get { lock (gate) return current; } }
    public int Subscribers { get { lock (gate) return listeners.Count; } }

    public IDisposable Subscribe(Action listener)
    {
        lock (gate) listeners.Add(listener);
        return new Unsubscribe(() => { lock (gate) listeners.Remove(listener); });
    }

    public void Publish(DurableView view)
    {
        Action[] targets;
        lock (gate) { current = view; targets = [.. listeners]; }
        foreach (var listener in targets) listener();
    }

    private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
}

/// <summary>A controller that records each request as text (for example <c>submit Steer hello</c>) and may react to it.</summary>
internal sealed class FakeController : IDurableController
{
    private readonly ConcurrentQueue<string> calls = new();
    public Action<string>? OnCall { get; set; }
    public IReadOnlyList<string> Calls => [.. calls];

    private Task Record(string call)
    {
        calls.Enqueue(call);
        OnCall?.Invoke(call);
        return Task.CompletedTask;
    }

    public Task SubmitAsync(string text, WhenBusy whenBusy) => Record($"submit {whenBusy} {text}");
    public Task CompactAsync(string? instructions) => Record(instructions is null ? "compact" : $"compact {instructions}");
    public Task AbortAsync() => Record("abort");
    public Task CycleThinkingAsync() => Record("cycleThinking");
    public Task SetModelAsync(ModelRef model) => Record($"setModel {model.Provider}/{model.ModelId}");
    public Task ToggleTasksAsync() => Record("toggleTasks");
    public Task SwitchConversationAsync(ConversationId id) => Record($"switch {id.Value}");

    public async Task WaitFor(string call, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!calls.Contains(call))
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Timed out waiting for the call \"{call}\". Calls: [{string.Join("; ", calls)}]");
            await Task.Delay(10);
        }
    }
}

internal sealed class FakeOpenDurable(IDurableViewSource view, IDurableController controller, PiSettings settings) : IOpenDurable
{
    public IDurableViewSource View { get; } = view;
    public IDurableController Controller { get; } = controller;
    public PiSettings Settings { get; } = settings;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The durable TUI running over fakes on a <see cref="VirtualTerminal"/>.</summary>
internal sealed class TuiRun : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(100));
    private readonly string agentDir;

    public VirtualTerminal Terminal { get; }
    public FakeViewSource Source { get; }
    public FakeController Controller { get; } = new();
    public Task<int> Run { get; }

    private TuiRun(DurableView initial, int columns, int rows, string settingsJson)
    {
        agentDir = Program.Temp("durable-tui");
        Themes.Environment = _ => null;
        Terminal = new VirtualTerminal(columns, rows);
        Source = new FakeViewSource(initial);
        var settings = PiSettings.FromObjects((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(settingsJson)!);
        var durable = new FakeOpenDurable(Source, Controller, settings);
        Run = Task.Run(() => DurableTui.RunAsync(durable, new DurableTuiOptions(loop => new ProcessTerminal(loop, Terminal, _ => null), agentDir),
            cancellation.Token));
    }

    /// <summary>Starts the TUI and waits for its first frame (the footer's hints).</summary>
    public static async Task<TuiRun> Start(DurableView initial, int columns = 120, int rows = 40, string settingsJson = "{}")
    {
        var run = new TuiRun(initial, columns, rows, settingsJson);
        await run.WaitFor("/compact · /tasks");
        return run;
    }

    public string Screen => Terminal.Text;

    /// <summary>Cancels the run's token, as the runtime's dispatch does on shutdown.</summary>
    public Task Cancel() => cancellation.CancelAsync();

    public Task<string> WaitFor(string needle, int timeoutMs = 20_000) => Guard(Terminal.WaitForTextAsync(needle, timeoutMs));

    public Task<string> WaitUntil(Func<string, bool> predicate, string what, int timeoutMs = 20_000) =>
        Guard(Terminal.WaitForAsync(predicate, what, timeoutMs));

    private async Task<string> Guard(Task<string> waiting)
    {
        if (await Task.WhenAny(waiting, Run) == Run && !waiting.IsCompleted)
        {
            await Run;
            throw new InvalidOperationException($"The TUI exited before the screen matched. Screen:\n{Screen}");
        }
        return await waiting;
    }

    public void Send(string data) => Terminal.Send(data);

    /// <summary>Types <paramref name="text"/>, then Enter, as InteractiveHarness.Submit does.</summary>
    public async Task Submit(string text)
    {
        Terminal.Send(text);
        await Task.Delay(30);
        Terminal.Send("\r");
    }

    public async Task<int> Exited(int timeoutMs = 20_000)
    {
        if (await Task.WhenAny(Run, Task.Delay(timeoutMs)) != Run) throw new TimeoutException($"The TUI did not exit. Screen:\n{Screen}");
        return await Run;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Run.IsCompleted)
        {
            Terminal.Send("\u0003");
            try { await Run.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { await cancellation.CancelAsync(); }
        }
        try { await Run; } catch { }
        cancellation.Dispose();
        try { Directory.Delete(agentDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
