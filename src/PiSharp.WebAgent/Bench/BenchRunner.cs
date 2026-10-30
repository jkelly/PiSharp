// Decision 0011 (run W4, R7): the Tool Bench's loop, on the server so it keeps going when the tab closes. Each model turn is one run of the
// run engine (RunEngine.StartAsync: the Spend Guard reserves it through GuardedRunExecutor, runs.db records it with source "bench"); every
// turn of a conversation shares one group id, so History shows a bench conversation as one group and a run cap covers the whole
// conversation. When a turn ends, its tool calls are checked against their tools' schemas (Pi's argument check) and answered by the
// auto-respond rules: the first mock whose match is a subset of the arguments, else a mock without a match; a call pauses the bench when
// auto-respond is off, when its arguments break the schema ("Pause on schema violations"), when no mock matches ("Pause when no mock
// matches"; off, the bench answers it with a "no mock" error result) or when it names a tool the bench did not send. When every call of a
// turn has a result, the next turn starts by itself (at most MaxAutoTurns in a row). A paused call is answered by hand ("Send result",
// "Send & continue"). The bench never runs a tool, built-ins included. A turn the Spend Guard refuses is not sent: from a request, the page
// gets the refusal (and the confirm modal); in the background, the session is "blocked" and Run asks again. One gate per session orders the
// user's actions and the loop. A turn's signed parts (its thinking blocks with their signatures, each call's thought signature) are kept
// with the turn and sent back with its calls, so a thinking model's tool loop continues on Anthropic and Google; Parallel tool calls off
// is the run's first-class switch, which the run engine sends where the API has one and refuses where it has none.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Bench;

/// <summary>An action the session's state does not allow now (HTTP 409).</summary>
public sealed class BenchConflictException(string message) : InvalidOperationException(message);

/// <summary>What an action left: the session, and the Spend Guard's refusal when a turn it tried to start was not sent.</summary>
public sealed record BenchAction(BenchSession Session, RunRefusal? Refusal = null);

/// <summary>What Run does with a conversation.</summary>
public enum BenchMode
{
    /// <summary>There is no user message.</summary>
    Empty,

    /// <summary>The next turn continues the conversation.</summary>
    Continue,

    /// <summary>The conversation ended (or stopped on calls without results): Run starts it again from the user messages.</summary>
    Restart,

    /// <summary>Calls wait for results.</summary>
    Pending,
}

/// <summary>How the bench answers one call.</summary>
/// <param name="Known">Whether the call names a tool the bench sent.</param>
/// <param name="Violations">Its schema violations.</param>
/// <param name="Pause">Why it pauses, or null.</param>
/// <param name="Result">Its automatic result, or null.</param>
public sealed record BenchDecision(bool Known, IReadOnlyList<BenchViolation> Violations, string? Pause, BenchResult? Result);

/// <summary>Runs bench conversations. A singleton.</summary>
public sealed class BenchRunner : IAsyncDisposable
{
    /// <summary>The most turns auto-respond starts in a row.</summary>
    public const int MaxAutoTurns = 25;

    /// <summary>The largest result the user sends.</summary>
    public const int MaxResultText = 1024 * 1024;

    /// <summary>The result auto-respond sends when no mock matches and the bench does not pause.</summary>
    public const string NoMockText = """{"error":"no_mock","message":"No saved mock result matches these arguments."}""";

    private const string StillEnding = "The stopped turn is still ending; try again in a moment.";

    private readonly BenchStore store;
    private readonly RunEngine engine;
    private readonly RunStore runs;
    private readonly IServiceProvider services;
    private readonly ILogger log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> watchers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    /// <summary>A runner over the bench store and the run engine.</summary>
    public BenchRunner(BenchStore store, RunEngine engine, RunStore runs, IServiceProvider services, ILogger<BenchRunner>? log = null,
        IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(services);
        this.store = store;
        this.engine = engine;
        this.runs = runs;
        this.services = services;
        this.log = (ILogger?)log ?? NullLogger.Instance;
        lifetime?.ApplicationStopping.Register(() =>
        {
            try { stopping.Cancel(); }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>The store.</summary>
    public BenchStore Store => store;

    /// <summary>The run engine.</summary>
    public RunEngine Engine => engine;

    /// <summary>The session <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is none.</exception>
    public BenchSession Get(string id) => store.Get(id) ?? throw new KeyNotFoundException($"There is no bench session {id}.");

    /// <summary>The most recent session, or a new one.</summary>
    public BenchSession Current() => store.Latest() ?? Create();

    /// <summary>A new session with <paramref name="setup"/> (or the default model and no tools) and <paramref name="messages"/> as its
    /// user messages.</summary>
    /// <exception cref="ArgumentException">The setup or a message is not valid.</exception>
    public BenchSession Create(BenchSetup? setup = null, IEnumerable<string>? messages = null)
    {
        var normalized = (setup ?? DefaultSetup()).Normalize();
        var now = DateTimeOffset.UtcNow;
        var entries = new List<BenchEntry>();
        foreach (var text in messages ?? [])
            entries.Add(new BenchEntry { Kind = "user", Text = Message(text), At = now });
        if (entries.Count > BenchSession.MaxEntries) throw new ArgumentException($"A conversation keeps at most {BenchSession.MaxEntries} entries.");
        return store.Add(new BenchSession { Id = "b" + Guid.NewGuid().ToString("N"), Created = now, Updated = now, Setup = normalized, Entries = entries });
    }

    /// <summary>The setup of a new session: the default chat model (or the first model PiSharp can run), no tools.</summary>
    public BenchSetup DefaultSetup()
    {
        try
        {
            if (services.GetService<ChatService>()?.NewChat() is { Provider: { } provider, Model: { } model } && engine.Capabilities(provider, model) is not null)
                return new BenchSetup { Provider = provider, Model = model };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogDebug(error, "The default model could not be read for the Tool Bench.");
        }
        var catalog = services.GetService<IModelCatalog>();
        var first = catalog?.Providers.SelectMany(provider => catalog.Models(provider.Id)).FirstOrDefault(model => engine.Capabilities(model.Provider, model.Id) is not null);
        return first is null ? new BenchSetup() : new BenchSetup { Provider = first.Provider, Model = first.Id };
    }

    /// <summary>What Run does with <paramref name="session"/>'s conversation.</summary>
    public static BenchMode Mode(BenchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Entries.Any(entry => entry.Kind == "user")) return BenchMode.Empty;
        if (session.Pending.Count > 0) return BenchMode.Pending;
        for (var index = session.Entries.Count - 1; index >= 0; index--)
        {
            var entry = session.Entries[index];
            if (entry.Kind == "user") return BenchMode.Continue;
            if (entry.Turn is { Completed: true } turn)
            {
                var calls = session.CallsOf(turn.Index);
                return calls.Count == 0 || calls.Any(call => call.Skipped) ? BenchMode.Restart : BenchMode.Continue;
            }
        }
        return BenchMode.Continue;
    }

    /// <summary>Whether the conversation stopped on calls that will never get a result (only Run, which starts again, or Reset go on).</summary>
    public static bool Broken(BenchSession session) => Mode(session) == BenchMode.Restart && session.Calls.Any(call => call.Skipped)
        && session.Entries.LastOrDefault(entry => entry.Kind == "user" || entry.Turn is { Completed: true }) is { Turn: { } turn }
        && session.CallsOf(turn.Index).Any(call => call.Skipped);

    /// <summary>Saves the setup (it applies from the next turn).</summary>
    public async Task<BenchSession> SaveSetupAsync(string id, BenchSetup setup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var normalized = setup.Normalize();
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            return Persist(session with { Setup = normalized });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs the conversation: continues it, or starts it again from the user messages when it ended.</summary>
    /// <exception cref="ArgumentException">The setup cannot run (no model, no tools, an option the model does not take).</exception>
    /// <exception cref="BenchConflictException">A turn runs, or calls wait for results.</exception>
    public async Task<BenchAction> RunAsync(string id, BenchSetup? setup = null, RunSpendAnswer? spend = null, CancellationToken cancellationToken = default)
    {
        var normalized = setup?.Normalize();
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            if (normalized is not null) session = Persist(session with { Setup = normalized });
            if (session.State == BenchStates.Running) throw new BenchConflictException("A turn is running. Stop it first.");
            var basis = Mode(session) switch
            {
                BenchMode.Empty => throw new ArgumentException("Add a user message first."),
                BenchMode.Pending => throw new BenchConflictException("Send a result for each pending tool call first, or reset the conversation."),
                BenchMode.Restart => session with
                {
                    Entries = [.. session.Entries.Where(entry => entry.Kind == "user")],
                    Calls = [],
                    Group = null,
                    PausedAt = null,
                },
                _ => session,
            };
            return await StartTurnLocked(basis with { AutoTurns = 0 }, spend, background: false, autoTurns: 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Adds a user message, and runs the conversation when <paramref name="run"/>.</summary>
    /// <exception cref="ArgumentException">The message is empty or too long, or the run cannot start.</exception>
    /// <exception cref="BenchConflictException">A turn runs, or calls wait for results.</exception>
    public async Task<BenchAction> AddMessageAsync(string id, string? text, bool run, BenchSetup? setup = null, RunSpendAnswer? spend = null,
        CancellationToken cancellationToken = default)
    {
        var message = Message(text);
        var normalized = setup?.Normalize();
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            if (normalized is not null) session = Persist(session with { Setup = normalized });
            if (session.State == BenchStates.Running) throw new BenchConflictException("A turn is running. Stop it first.");
            if (session.Pending.Count > 0) throw new BenchConflictException("Send the pending tool result first.");
            if (run && session.ActiveRun is not null) throw new BenchConflictException(StillEnding);
            if (Broken(session)) throw new BenchConflictException("This conversation stopped on tool calls without results. Reset the conversation before adding a message.");
            if (session.Entries.Count >= BenchSession.MaxEntries) throw new ArgumentException($"A conversation keeps at most {BenchSession.MaxEntries} entries; reset it.");
            var saved = Persist(session with
            {
                Entries = [.. session.Entries, new BenchEntry { Kind = "user", Text = message, At = DateTimeOffset.UtcNow }],
                State = BenchStates.Idle,
                Note = null,
                SpendCode = null,
                AutoTurns = 0,
            });
            if (!run) return new BenchAction(saved);
            return await StartTurnLocked(saved, spend, background: false, autoTurns: 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes the user message at entry <paramref name="index"/> and everything after it.</summary>
    /// <exception cref="BenchConflictException">A turn runs, or calls wait for results.</exception>
    public async Task<BenchSession> RemoveMessageAsync(string id, int index, CancellationToken cancellationToken = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            if (session.State is BenchStates.Running or BenchStates.Awaiting) throw new BenchConflictException("Stop the run before you change the conversation.");
            if (index < 0 || index >= session.Entries.Count || session.Entries[index].Kind != "user") throw new KeyNotFoundException($"There is no user message {index}.");
            var entries = session.Entries.Take(index).ToList();
            var turns = entries.Where(entry => entry.Turn is not null).Select(entry => entry.Turn!.Index).ToHashSet();
            var next = session with
            {
                Entries = entries,
                Calls = [.. session.Calls.Where(call => turns.Contains(call.Turn))],
                Group = entries.Count == 0 ? null : session.Group,
                Note = null,
                SpendCode = null,
                PausedAt = null,
                AutoTurns = 0,
            };
            return Persist(next with { State = Settle(next) });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Records the user's result for a paused call; with <paramref name="continue"/>, runs the next turn once every call has one.</summary>
    /// <exception cref="KeyNotFoundException">There is no such call.</exception>
    /// <exception cref="BenchConflictException">The call does not wait for a result.</exception>
    public async Task<BenchAction> SubmitResultAsync(string id, string callId, string? text, bool isError, string? mock, bool @continue,
        RunSpendAnswer? spend = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callId);
        var result = text ?? "";
        if (result.Length > MaxResultText) throw new ArgumentException($"A result is at most {MaxResultText / 1024} KB.");
        var mockName = string.IsNullOrWhiteSpace(mock) ? null : mock.Trim();
        if (mockName is { Length: > 80 }) throw new ArgumentException("A mock's name is at most 80 characters.");
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            var call = session.Calls.FirstOrDefault(item => item.Id == callId && item.Pending);
            if (call is null)
            {
                if (session.Calls.Any(item => item.Id == callId)) throw new BenchConflictException($"Call {callId} does not wait for a result.");
                throw new KeyNotFoundException($"There is no call {callId}.");
            }
            if (session.State == BenchStates.Running) throw new BenchConflictException("A turn is running. Wait for it, or stop it.");
            var now = DateTimeOffset.UtcNow;
            var answered = call with
            {
                Result = new BenchResult
                {
                    Text = result, IsError = isError, Source = BenchResultSources.Manual, Mock = mockName, At = now,
                    Ms = Math.Max(0, (long)(now - call.At).TotalMilliseconds),
                },
            };
            var calls = session.Calls.Select(item => ReferenceEquals(item, call) ? answered : item).ToList();
            var next = session with { Calls = calls, AutoTurns = 0, SpendCode = null };
            if (next.Pending.Count > 0) return new BenchAction(Persist(next with { State = BenchStates.Awaiting }));
            var saved = Persist(next with
            {
                State = BenchStates.Idle,
                PausedAt = null,
                Note = @continue ? null : "Every call has its result. Run continues the conversation.",
            });
            if (!@continue) return new BenchAction(saved);
            return await StartTurnLocked(saved, spend, background: false, autoTurns: 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops the bench: cancels the running turn, or ends the pause (the calls waiting for results never get one).</summary>
    public async Task<BenchSession> StopAsync(string id, CancellationToken cancellationToken = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            if (loops.TryRemove(id, out var loop))
            {
                try { await loop.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
                loop.Dispose();
            }
            switch (session.State)
            {
                case BenchStates.Running:
                    if (session.ActiveRun is { } run) engine.Cancel(run);
                    return Persist(session with { State = BenchStates.Stopped, Note = "Stopped. Run continues the conversation, or starts it again if a call has no result." });
                case BenchStates.Awaiting:
                    var skipped = session.Calls.Select(call => call.Pending ? call with { Skipped = true, Pause = call.Pause } : call).ToList();
                    return Persist(session with
                    {
                        Calls = skipped,
                        State = BenchStates.Stopped,
                        PausedAt = null,
                        Note = "Stopped while a call waited for its result. Run starts the conversation again from your messages.",
                    });
                case BenchStates.Blocked:
                    return Persist(session with { State = BenchStates.Stopped, Note = "Stopped. Run asks the Spend Guard again." });
                default:
                    return session;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Clears the model's turns and the tool results; keeps the user messages and the setup.</summary>
    /// <exception cref="BenchConflictException">A turn runs.</exception>
    public async Task<BenchSession> ResetAsync(string id, CancellationToken cancellationToken = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Get(id);
            if (session.State == BenchStates.Running) throw new BenchConflictException("A turn is running. Stop it first.");
            return Persist(session with
            {
                Entries = [.. session.Entries.Where(entry => entry.Kind == "user")],
                Calls = [],
                Group = null,
                State = BenchStates.Idle,
                Note = null,
                SpendCode = null,
                PausedAt = null,
                ActiveRun = null,
                AutoTurns = 0,
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Waits until session <paramref name="id"/> is not running and its turns are recorded (for tests and shutdown).</summary>
    public async Task<BenchSession> WhenSettledAsync(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var session = Get(id);
            var watching = session.Turns.Any(turn => watchers.ContainsKey(turn.RunId));
            if (session.State != BenchStates.Running && !watching) return session;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Bench session {id} is still {session.State}.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>How the bench answers a call of <paramref name="name"/> with <paramref name="arguments"/> under <paramref name="setup"/>.</summary>
    public static BenchDecision Decide(BenchSetup setup, string name, JsonNode? arguments)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(name);
        var rules = setup.Rules;
        var tool = setup.Tools.FirstOrDefault(item => item.Enabled && item.Name == name);
        if (tool is null)
        {
            IReadOnlyList<BenchViolation> unknown = [new BenchViolation("/", $"The model called {name}, which is not one of the tools the bench sent.")];
            if (!rules.AutoRespond) return new BenchDecision(false, unknown, BenchPauses.Manual, null);
            if (rules.PauseOnSchema || rules.PauseOnNoMock) return new BenchDecision(false, unknown, BenchPauses.UnknownTool, null);
            return new BenchDecision(false, unknown, null, NoMockResult(rules));
        }
        var violations = BenchSchema.CheckArguments(name, tool.Parameters, arguments, tool.Builtin);
        if (!rules.AutoRespond) return new BenchDecision(true, violations, BenchPauses.Manual, null);
        if (violations.Count > 0 && rules.PauseOnSchema) return new BenchDecision(true, violations, BenchPauses.Schema, null);
        var mock = tool.Mocks.FirstOrDefault(item => !item.Fallback && BenchSchema.Matches(item.Match, arguments)) ?? tool.Mocks.FirstOrDefault(item => item.Fallback);
        if (mock is not null)
            return new BenchDecision(true, violations, null, new BenchResult
            {
                Text = mock.Text, IsError = mock.IsError, Source = BenchResultSources.Mock, Mock = mock.Name, Ms = BenchLatency.Delay(rules.Latency),
            });
        if (rules.PauseOnNoMock) return new BenchDecision(true, violations, BenchPauses.NoMock, null);
        return new BenchDecision(true, violations, null, NoMockResult(rules));
    }

    /// <summary>The run spec of the next turn of <paramref name="session"/> (what it would send).</summary>
    /// <exception cref="ArgumentException">The setup cannot run.</exception>
    public RunSpec Spec(BenchSession session, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var setup = session.Setup;
        if (setup.Provider.Length == 0 || setup.Model.Length == 0) throw new ArgumentException("Choose a model.");
        if (setup.Tools.Count == 0) throw new ArgumentException("Define a tool first: the bench sends your tools with every request.");
        var enabled = setup.Enabled;
        if (enabled.Count == 0) throw new ArgumentException("Switch on at least one tool: the bench sends only the tools that are on.");
        _ = engine.Capabilities(setup.Provider, setup.Model) ?? throw new ArgumentException($"There is no model {setup.Provider}/{setup.Model}; choose another.");
        var choice = setup.ToolChoice == "any" ? "required" : setup.ToolChoice;
        return new RunSpec
        {
            Provider = setup.Provider,
            Model = setup.Model,
            Account = setup.Account,
            Source = SpendSource.Bench.Wire(),
            GroupId = group ?? session.Group,
            SystemPrompt = setup.SystemPrompt,
            Messages = Messages(session),
            Tools = [.. enabled.Select(tool => new RunTool(tool.Name, tool.Description, (JsonObject?)tool.Parameters?.DeepClone()))],
            ToolChoice = choice,
            Thinking = setup.Thinking,
            // Off is sent where the API has a switch (parallel_tool_calls, or Anthropic's disable_parallel_tool_use); the run engine refuses it
            // on an API without one, so the request never allows parallel calls the user switched off.
            ParallelToolCalls = setup.Parallel || choice == "none" ? null : false,
        };
    }

    /// <summary>The messages of <paramref name="session"/>'s conversation: user messages, completed turns with their calls, and the calls'
    /// results (a failed or stopped turn is left out).</summary>
    public static IReadOnlyList<RunMessage> Messages(BenchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var messages = new List<RunMessage>();
        foreach (var entry in session.Entries)
        {
            if (entry.Kind == "user")
            {
                messages.Add(new RunMessage { Role = "user", Text = entry.Text ?? "" });
                continue;
            }
            if (entry.Turn is not { Completed: true } turn) continue;
            var calls = session.CallsOf(turn.Index);
            if (calls.Count == 0 && string.IsNullOrEmpty(turn.Text)) continue;
            messages.Add(new RunMessage
            {
                Role = "assistant",
                Text = string.IsNullOrEmpty(turn.Text) ? null : turn.Text,
                // The provider's signed parts go back as they came, so a thinking model's tool loop continues (Anthropic's thinking blocks,
                // Google's thought signatures).
                Thinking = turn.ThinkingBlocks is { Count: > 0 } blocks ? blocks : null,
                ToolCalls = calls.Count == 0 ? null
                    : [.. calls.Select(call => new RunToolCall(call.Id, call.Tool, (JsonObject?)call.Arguments?.DeepClone() ?? new JsonObject(), call.ThoughtSignature))],
            });
            foreach (var call in calls)
                if (call.Result is { } result)
                    messages.Add(new RunMessage { Role = "toolResult", ToolCallId = call.Id, ToolName = call.Tool, Text = result.Text, IsError = result.IsError });
        }
        return messages;
    }

    /// <summary>Stops the loop (running turns are stopped by the run engine and read back interrupted).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await stopping.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        try { await Task.WhenAll(watchers.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { log.LogDebug("Bench turns were still being recorded when the app stopped."); }
        foreach (var loop in loops.Values) loop.Dispose();
        stopping.Dispose();
    }

    private static BenchResult NoMockResult(BenchRules rules) =>
        new() { Text = NoMockText, IsError = true, Source = BenchResultSources.NoMock, Ms = BenchLatency.Delay(rules.Latency) };

    private static string Message(string? text)
    {
        var message = text?.Trim() ?? "";
        if (message.Length == 0) throw new ArgumentException("Write a message first.");
        if (message.Length > BenchSession.MaxMessage) throw new ArgumentException($"A message is at most {BenchSession.MaxMessage:N0} characters.");
        return message;
    }

    private static string Settle(BenchSession session)
    {
        if (session.Pending.Count > 0) return BenchStates.Awaiting;
        if (Broken(session)) return BenchStates.Stopped;
        return Mode(session) == BenchMode.Restart ? BenchStates.Finished : BenchStates.Idle;
    }

    private SemaphoreSlim Gate(string id) => gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private BenchSession Persist(BenchSession next) =>
        store.Update(next.Id, _ => next with { }) ?? throw new KeyNotFoundException($"There is no bench session {next.Id}.");

    // Starts the next turn of `basis` (the caller holds the session's gate). A request's refusal changes nothing; a background one blocks
    // the session, and a background error ends it in error.
    private async Task<BenchAction> StartTurnLocked(BenchSession basis, RunSpendAnswer? spend, bool background, int autoTurns, CancellationToken cancellationToken)
    {
        if (!background && basis.ActiveRun is not null) throw new BenchConflictException(StillEnding);
        var group = basis.Group ?? "g" + Guid.NewGuid().ToString("N");
        var index = basis.Turns.Count == 0 ? 1 : basis.Turns.Max(turn => turn.Index) + 1;
        RunStart start;
        try
        {
            var spec = Spec(basis, group);
            start = await engine.StartAsync(spec, spend, background ? stopping.Token : cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException error) when (background)
        {
            return new BenchAction(Persist(basis with { State = BenchStates.Error, ActiveRun = null, Note = error.Message, Group = group }));
        }
        if (!start.Posted)
        {
            var refusal = start.Refusal!;
            if (!background) return new BenchAction(store.Get(basis.Id) ?? basis, refusal);
            return new BenchAction(Persist(basis with
            {
                State = BenchStates.Blocked, ActiveRun = null, Group = group, SpendCode = refusal.Code,
                Note = "The Spend Guard did not let the next turn through: " + refusal.Message,
            }), refusal);
        }
        var runId = start.Id!;
        var now = DateTimeOffset.UtcNow;
        var turn = new BenchTurn { Index = index, RunId = runId, Status = RunStatus.Running, Started = now };
        var running = Persist(basis with
        {
            Entries = [.. basis.Entries, new BenchEntry { Kind = "assistant", At = now, Turn = turn }],
            Group = group,
            State = BenchStates.Running,
            ActiveRun = runId,
            Note = null,
            SpendCode = null,
            PausedAt = null,
            AutoTurns = autoTurns,
        });
        Watch(running.Id, runId);
        return new BenchAction(running);
    }

    private void Watch(string sessionId, string runId)
    {
        Task task;
        using (ExecutionContext.SuppressFlow()) task = Task.Run(() => WatchAsync(sessionId, runId));
        watchers[runId] = task;
        _ = task.ContinueWith(_ => watchers.TryRemove(KeyValuePair.Create(runId, task)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task WatchAsync(string sessionId, string runId)
    {
        try
        {
            if (engine.Completion(runId) is { } running)
            {
                try { await running.WaitAsync(stopping.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { log.LogDebug(error, "Bench run {Run} ended with an error.", runId); }
            }
            var row = runs.Get(runId);
            for (var tries = 0; row is { Status: RunStatus.Running } && tries < 400; tries++)
            {
                await Task.Delay(25, stopping.Token).ConfigureAwait(false);
                row = runs.Get(runId);
            }
            var outcome = TurnOutcome.Of(row);
            var snapshot = store.Get(sessionId);
            if (snapshot is null) return;
            IReadOnlyList<BenchDecision>? decisions = null;
            if (outcome.Completed && snapshot.State == BenchStates.Running && snapshot.ActiveRun == runId)
            {
                decisions = [.. outcome.Calls.Select(call => Decide(snapshot.Setup, call.Name, call.Arguments))];
                var delay = decisions.Where(decision => decision.Result is not null).Select(decision => decision.Result!.Ms).DefaultIfEmpty(0).Max();
                if (delay > 0)
                {
                    var loop = loops.GetOrAdd(sessionId, _ => new CancellationTokenSource());
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(loop.Token, stopping.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(delay), linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
                    catch (ObjectDisposedException) { }
                }
            }
            var gate = Gate(sessionId);
            await gate.WaitAsync(stopping.Token).ConfigureAwait(false);
            try
            {
                await FinishLocked(sessionId, runId, outcome, decisions).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The app is stopping: the session reads back interrupted at the next start.
        }
        catch (Exception error)
        {
            log.LogError(error, "Bench session {Session} could not record run {Run}.", sessionId, runId);
            try
            {
                store.Update(sessionId, session => session.ActiveRun == runId
                    ? session with { State = BenchStates.Error, ActiveRun = null, Note = "The bench could not record this turn: " + error.Message }
                    : session);
            }
            catch (Exception again) when (again is not OutOfMemoryException)
            {
                log.LogError(again, "Bench session {Session} could not be marked failed.", sessionId);
            }
        }
    }

    private async Task FinishLocked(string sessionId, string runId, TurnOutcome outcome, IReadOnlyList<BenchDecision>? decisions)
    {
        var session = store.Get(sessionId);
        if (session is null) return;
        var position = -1;
        for (var index = 0; index < session.Entries.Count; index++)
            if (session.Entries[index].Turn?.RunId == runId) position = index;
        // Reset (or a restart) dropped the turn: nothing to record.
        if (position < 0)
        {
            if (session.ActiveRun == runId) Persist(session with { ActiveRun = null, State = Settle(session) });
            return;
        }
        var stopped = session.State != BenchStates.Running || session.ActiveRun != runId;
        var turn = session.Entries[position].Turn! with
        {
            Status = outcome.Status,
            StopReason = outcome.StopReason,
            DurationMs = outcome.DurationMs,
            Text = outcome.Text,
            Thinking = outcome.Thinking,
            ThinkingBlocks = outcome.Blocks,
            Calls = outcome.Completed ? [.. outcome.Calls.Select(call => call.Id)] : [],
            Input = outcome.Usage.Input,
            Output = outcome.Usage.Output,
            CacheRead = outcome.Usage.CacheRead,
            CostMicros = outcome.CostMicros,
            CostKind = outcome.CostKind,
            Error = outcome.Error,
        };
        var entries = session.Entries.ToList();
        entries[position] = entries[position] with { Turn = turn };
        var at = turn.Started.AddMilliseconds(outcome.DurationMs ?? 0);
        var now = DateTimeOffset.UtcNow;
        var calls = new List<BenchCall>();
        if (outcome.Completed)
        {
            for (var index = 0; index < outcome.Calls.Count; index++)
            {
                var made = outcome.Calls[index];
                var decision = decisions is { } list && index < list.Count ? list[index] : Decide(session.Setup, made.Name, made.Arguments);
                var call = new BenchCall
                {
                    Id = made.Id,
                    Tool = made.Name,
                    Turn = turn.Index,
                    Index = index + 1,
                    Count = outcome.Calls.Count,
                    Arguments = made.Arguments as JsonObject,
                    RawArguments = made.Arguments is JsonObject ? null : made.Arguments?.ToJsonString(),
                    ThoughtSignature = made.Signature,
                    Known = decision.Known,
                    Violations = decision.Violations,
                    At = at,
                };
                if (stopped) call = call with { Skipped = true };
                else if (decision.Result is { } result) call = call with { Result = result with { At = now } };
                else call = call with { Pause = decision.Pause };
                calls.Add(call);
            }
        }
        var next = session with
        {
            Entries = entries,
            Calls = [.. session.Calls, .. calls],
            ActiveRun = session.ActiveRun == runId ? null : session.ActiveRun,
        };
        if (stopped)
        {
            Persist(next);
            return;
        }
        if (!outcome.Completed)
        {
            var aborted = outcome.Status is RunStatus.Aborted or RunStatus.Interrupted;
            Persist(next with
            {
                State = aborted ? BenchStates.Stopped : BenchStates.Error,
                Note = aborted ? outcome.Error ?? "The turn was stopped." : outcome.Error ?? "The turn failed.",
            });
            return;
        }
        if (calls.Count == 0)
        {
            Persist(next with { State = BenchStates.Finished, Note = null });
            return;
        }
        if (calls.Any(call => call.Pause is not null))
        {
            Persist(next with { State = BenchStates.Awaiting, PausedAt = now, Note = null });
            return;
        }
        if (session.AutoTurns + 1 > MaxAutoTurns)
        {
            Persist(next with { State = BenchStates.Stopped, Note = $"Stopped after {MaxAutoTurns} automatic turns in a row. Run continues the conversation." });
            return;
        }
        await StartTurnLocked(next, null, background: true, autoTurns: session.AutoTurns + 1, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What a finished run of a turn said.</summary>
    internal sealed record TurnOutcome(string Status, string? StopReason, long? DurationMs, string? Text, string? Thinking,
        IReadOnlyList<(string Id, string Name, JsonNode? Arguments, string? Signature)> Calls, RunUsage Usage, long? CostMicros, string? CostKind, string? Error,
        IReadOnlyList<RunThinking>? Blocks = null)
    {
        public bool Completed => Status == RunStatus.Completed;

        public static TurnOutcome Of(RunRecord? row)
        {
            if (row is null) return new TurnOutcome(RunStatus.Error, null, null, null, null, [], RunUsage.Zero, null, null, "The turn's run was not recorded.");
            var text = new System.Text.StringBuilder();
            var thinking = new System.Text.StringBuilder();
            var blocks = new List<RunThinking>();
            var calls = new List<(string, string, JsonNode?, string?)>();
            if (row.Output?["content"] is JsonArray content)
            {
                foreach (var block in content.OfType<JsonObject>())
                {
                    var type = block["type"] is JsonValue kind && kind.TryGetValue<string>(out var said) ? said : null;
                    switch (type)
                    {
                        case "text" when block["text"] is JsonValue value && value.TryGetValue<string>(out var part):
                            text.Append(part);
                            break;
                        case "thinking" when block["thinking"] is JsonValue value && value.TryGetValue<string>(out var thought):
                            if (thought.Length > 0)
                            {
                                if (thinking.Length > 0) thinking.Append("\n\n");
                                thinking.Append(thought);
                            }
                            blocks.Add(new RunThinking(thought, Field(block, "thinkingSignature"),
                                block["redacted"] is JsonValue redacted && redacted.TryGetValue<bool>(out var isRedacted) && isRedacted));
                            break;
                        case "toolCall":
                            var id = block["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var callId) ? callId : "";
                            var name = block["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var toolName) ? toolName : "";
                            if (id.Length > 0 && name.Length > 0) calls.Add((id, name, block["arguments"]?.DeepClone(), Field(block, "thoughtSignature")));
                            break;
                    }
                }
            }
            else if (row.OutputText is { } partial) text.Append(partial);
            return new TurnOutcome(row.Status, row.StopReason, row.DurationMs, text.Length == 0 ? null : text.ToString(),
                thinking.Length == 0 ? null : thinking.ToString(), calls, row.Usage, row.CostMicros, row.CostKind, row.Error, blocks.Count == 0 ? null : blocks);
        }

        private static string? Field(JsonObject block, string name) =>
            block[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
    }
}
