// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/codemode/codemode.ts and codemode/index.ts, the
// `@earendil-works/pi-durable/codemode` entry point. Upstream's CODEMODE_MODULE_VERSION, CODEMODE_TOOL_NAME, createCodemodeTool,
// createCodemodeExtension, CodemodeStoreDoc, CodemodeRunDoc and CodemodeJournalDoc are DurableCodemode.ModuleVersion, ToolName, CreateTool,
// CreateExtension, StoreDoc, RunDoc and JournalDoc; CodemodeToolOptions, CodemodeGlobal and CodemodeNamespace keep their names; the
// CodemodeToolInput type is the tool's `{ code }` arguments, and CodemodeToolDetails and CodemodeNestedCall are the result's details JSON
// (`{ calls: [{ id, name, args, status, durationMs?, error? }], fullOutputPath? }`).
//
// Mappings:
// - The sandbox is PiSharp.Codemode's CodemodeSandbox and its deterministic Start() (decision 0005, Codemode Start()): CodemodeRun's Deliver,
//   Finished and AbortAsync, CodemodeHostCall, CodemodeOutcome and CodemodeResult.During. Its fingerprint names Jint, so a script started
//   under Pi never resumes under PiSharp or the other way round: the fingerprint check refuses it (an intentional difference).
// - One call's state is touched only on its own serial scheduler (an exclusive TaskScheduler), as upstream's lives on Node's single thread:
//   the sandbox's callbacks, which arrive on the engine's reader thread, are posted to it in order, and every await resumes on it. Upstream's
//   `void promise.then(...)` chains are async methods started on it.
// - The AbortController is Controller: AbortAsync on the run once it started, else a cancelled token and AbortMessage for Start. An abort's
//   reason becomes the run's abort message.
// - JSON crosses between the durable core's JsonValue and the sandbox's JsonData by its text; `undefined` is a C# null on both sides.
// - The calls launchCovered() launches together are admitted together (ToolExecutionApi.AdmitTogether): upstream's executeTool() calls of
//   one synchronous stretch enqueue their admission commits back to back, while the Harness admits calls a tool makes after an await one at
//   a time. Script arguments that are not an object reach the nested call as they are (ToolExecutionApi.ExecuteToolAsync over a JsonValue).
// - Date.now() is the system clock in whole milliseconds; crypto.getRandomValues is RandomNumberGenerator.
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>A function scripts call as a top-level global; its result is journaled (upstream <c>CodemodeGlobal</c>). Null is undefined, both
/// for the arguments and the result.</summary>
public sealed record CodemodeGlobal(string Name, Func<JsonValue?, IToolExecutionApi, DurableContext, Task<JsonValue?>> Execute)
{
    /// <summary>Whether the global receives all its arguments as an array.</summary>
    public bool Spread { get; init; }
}

/// <summary>Options of the code mode tool and extension (upstream <c>CodemodeToolOptions</c>).</summary>
public sealed record CodemodeToolOptions
{
    /// <summary>Runs the scripts.</summary>
    public required CodemodeSandbox Sandbox { get; init; }
    /// <summary>Groups of tools by name prefix, such as MCP servers, for the <c>codemode</c> section and tool search.</summary>
    public IReadOnlyList<CodemodeNamespace>? Namespaces { get; init; }
    /// <summary>Estimated tokens the tool declarations in the <c>codemode</c> section may use. Default 3000.</summary>
    public int? InlineBudget { get; init; }
    /// <summary>Give scripts <c>models.*</c>: the model catalog, classifiers, and image generation. Default true.</summary>
    public bool? Models { get; init; }
    /// <summary>Host functions scripts call as globals (upstream <c>@internal</c>).</summary>
    internal IReadOnlyList<CodemodeGlobal>? Globals { get; init; }
    /// <summary>Test seam: called with each run right after <see cref="CodemodeSandbox.Start"/> returns it, as upstream's tests wrap
    /// <c>sandbox.start</c> to count and observe runs.</summary>
    internal Action<CodemodeRun>? OnRunStarted { get; init; }
}

/// <summary>
/// Durable code mode (upstream <c>codemode.ts</c>): a tool that runs model-written JavaScript in a sandbox, whose script survives a restart.
/// <para>Problem: a script is in-memory VM state. A restart while <c>await Promise.all(paths.map((path) =&gt; tools.read({ path })))</c> waits
/// for the last of 200 reads would lose the script and every read it made.</para>
/// <para>Solution: deterministic replay (<see cref="CodemodeSandbox.Start"/>). The VM only runs when the host hands it a result; given the same
/// start data, clock, seed, and results in the same order, it makes the same calls with the same arguments. The tool is replay-safe: after a
/// restart, its execute runs again, starts the script again from the stored start data, and hands it the results it got before, in the order
/// it got them, from a journal written before each delivery. Nested tool calls reattach to their tasks by key (<c>c&lt;VM call id&gt;</c>), so
/// they never run twice unless their own replay policy reruns them; their results come from the nested call document. Then the script
/// continues live.</para>
/// <para>Documents, scoped to the code mode call's task and gone when it settles: <see cref="RunDoc"/> holds the start data, written once
/// before the VM starts, and the outcome, written once when the call decides it (with the store writes, in one transaction), so a rerun after
/// that returns it without a VM; <see cref="JournalDoc"/> holds one entry per delivered result, in delivery order: the VM call ID, the clock
/// value, the calls the VM had issued after processing it (once acknowledged), and a global's outcome, which nothing else stores.</para>
/// <para>Each result's acknowledged call count is recorded with the next journal commit, or at once when the VM has processed everything
/// delivered. A call is launched only once the count of calls issued in the drain that made it is committed, so every key an earlier attempt
/// admitted is at most its last recorded count; a replay checks every recorded count, so when it releases its calls it has made every call an
/// earlier attempt could have admitted, and later calls are new.</para>
/// <para>A rerun refuses to replay when this module's version or the VM's fingerprint changed, when the deadline passed, or when the script
/// diverges: it makes another call, or calls in another order, than the journal says. Each ends the call with an error result.</para>
/// </summary>
public static class DurableCodemode
{
    /// <summary>Version of everything in this module that turns stored results into what a script sees: result mapping, globals, start data
    /// (upstream <c>CODEMODE_MODULE_VERSION</c>). Bumped when that changes, so a call started before an upgrade is not replayed by different
    /// code.</summary>
    public const int ModuleVersion = 2;

    /// <summary><c>codemode</c> (upstream <c>CODEMODE_TOOL_NAME</c>).</summary>
    public const string ToolName = "codemode";

    /// <summary>Default token budget of the tool declarations in the <c>codemode</c> section (upstream
    /// <c>DEFAULT_CODEMODE_INLINE_BUDGET</c>).</summary>
    public const int DefaultInlineBudget = CodemodeDescribe.DefaultInlineBudget;

    /// <summary><c>pi.codemode.run</c> (task scope): <c>{ start?, outputDir?, result? }</c>.</summary>
    internal static TaskDocToken RunDoc { get; } = Documents.DefineTaskDoc("pi.codemode.run", 1, () => JsonObjectValue.Empty);

    /// <summary><c>pi.codemode.journal</c> (task scope): <c>{ entries: [{ id, at, issuedAfter?, global? }], firstIssued? }</c>.</summary>
    internal static TaskDocToken JournalDoc { get; } =
        Documents.DefineTaskDoc("pi.codemode.journal", 1, () => (JsonObjectValue)JsonValue.Parse("""{"entries":[]}"""));

    /// <summary>
    /// What scripts keep with <c>store()</c> and read with <c>load()</c>, per conversation (upstream <c>CodemodeStoreDoc</c>,
    /// <c>pi.codemode.store</c>, <c>{ values }</c>). Rewindable and copied as of the fork point, so a fork sees the values as they were when its
    /// entry was committed.
    /// </summary>
    public static RewindableConversationDocToken StoreDoc { get; } = Documents.DefineRewindableConversationDoc("pi.codemode.store", 1, DocumentFork.AsOf,
        () => (JsonObjectValue)JsonValue.Parse("""{"values":{}}"""));

    /// <summary><c>Type.Object({ code: Type.String({ description: "Raw JavaScript source." }) })</c>, as TypeBox prints it.</summary>
    internal static readonly JsonObjectValue Parameters = (JsonObjectValue)JsonValue.Parse(
        """{"type":"object","required":["code"],"properties":{"code":{"type":"string","description":"Raw JavaScript source."}}}""");

    /// <summary>Outputs bounded by the script's own token budget; the Harness's limits only stop a runaway.</summary>
    internal static readonly CodemodeOutput.Limits OutputLimits = new(4 * 1024 * 1024, 100_000);

    /// <summary>The code mode tool (upstream <c>createCodemodeTool</c>). Scripts call the conversation's callable tools as
    /// <c>tools.&lt;name&gt;(args)</c>. Only the model calls it; it is replay-safe.</summary>
    public static ToolRegistration CreateTool(CodemodeToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Define.Tool(new ToolRegistration
        {
            Name = ToolName,
            Description = CodemodeDescribe.Description(options.Models != false),
            Parameters = Parameters,
            OutputLimits = new ToolOutputLimits { MaxBytes = OutputLimits.MaxBytes, MaxLines = OutputLimits.MaxLines },
            // Capable models write the script as raw text instead of a JSON-escaped string; pi-ai applies it only for them.
            ConstrainedSampling = JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"grammar"),
                KeyValuePair.Create("variants", (JsonValue)JsonValue.Object([KeyValuePair.Create("openai_lark", (JsonValue)CodemodeSource.Grammar)]))]),
            Callers = [ToolCaller.Model],
            Replay = ToolReplay.Safe,
            Execute = (arguments, api, context) =>
                new DurableCodemodeCall(options, api, context).ExecuteAsync(((JsonStringValue)((JsonObjectValue)arguments)["code"]).Value),
        });
    }

    /// <summary>The <c>codemode</c> extension (upstream <c>createCodemodeExtension</c>): the code mode tool, the internal tools behind
    /// <c>models.*</c>, and the <c>codemode</c> prompt section listing the tools scripts can call.</summary>
    public static Extension CreateExtension(CodemodeToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var namespaces = options.Namespaces ?? [];
        var budget = options.InlineBudget ?? DefaultInlineBudget;
        return Define.Extension(new Extension
        {
            Name = "codemode",
            Tools = [CreateTool(options), .. options.Models == false ? [] : CodemodeModels.CreateModelTools()],
            Sections =
            [
                Define.Section("codemode", (input, _) => ValueTask.FromResult(CodemodeDescribe.Section(
                    // Deferred tools are found with `searchTools()`, as the coding agent leaves them out too.
                    [.. input.Agent.Callable.Where(tool => !tool.Name.StartsWith(CodemodeModels.InternalPrefix, StringComparison.Ordinal) && !tool.Deferred)],
                    namespaces, budget))),
            ],
        });
    }
}

/// <summary>One execution of the code mode tool: a first run, or a rerun after a restart that replays the journal (upstream
/// <c>CodemodeCall</c>).</summary>
internal sealed class DurableCodemodeCall
{
    private const int ArgsPreviewChars = 200;
    private const int ErrorPreviewChars = 500;
    private const int DefaultSearchLimit = 8;
    /// <summary>Globals for finding tools, answered from the start data, so a replay sees what the first run saw.</summary>
    private static readonly ImmutableArray<string> DiscoveryGlobals = ["searchTools", "describeTool", "describeNamespace"];

    /// <summary>One tool as the script sees it: <c>tools.&lt;jsName&gt;</c>. <c>Structured</c>: the tool declares a structured output schema,
    /// so an error result with a value resolves with it. <c>Hidden</c>: an internal tool of this extension, which scripts reach only through a
    /// global such as <c>models.classify()</c>.</summary>
    private sealed record DeclaredTool(string Name, string JsName, string Description, bool Structured, bool Hidden);

    private sealed record NamespaceEntry(string Name, string? Description, string? Instructions, ImmutableArray<string> Tools);

    /// <summary>What a call starts with; a rerun starts the VM from exactly this (the JSON is upstream's <c>Start</c>).</summary>
    private sealed record StartData(JsonNumberValue Version, double StartedAt, uint Seed, string? Fingerprint, double? Deadline,
        ImmutableArray<DeclaredTool> Tools, ImmutableArray<NamespaceEntry> Namespaces, ImmutableArray<(string Name, bool Spread)> Globals,
        long MaxOutputTokens, JsonObjectValue Store);

    private sealed record GlobalEntry(string Name, JsonValue? Args, JsonObjectValue Outcome);

    /// <summary>The VM's call <see cref="Id"/>, whose result is the entry's position + 1; the VM clock while it processes it; the calls the VM
    /// had issued after processing it, once acknowledged; a global's call and outcome.</summary>
    private sealed record JournalEntry(long Id, double At, long? IssuedAfter, GlobalEntry? Global);

    private sealed class Row(long id, string name, string args)
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public string Args { get; } = args;
        public string Status { get; set; } = "running";
        public double? DurationMs { get; set; }
        public string? Error { get; set; }
    }

    private sealed record Finished(long Id, double At, CodemodeOutcome Outcome, JsonObjectValue? Global);

    private readonly CodemodeToolOptions options;
    private readonly IToolExecutionApi api;
    private readonly DurableContext context;
    /// <summary>The call's serial scheduler (see the file header).</summary>
    private readonly TaskScheduler loop = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
    private CodemodeRun? run;
    private StartData start = null!;
    private bool controllerAborted;
    private string? controllerReason;
    /// <summary>The journal as this attempt found it; replay delivers exactly these.</summary>
    private ImmutableArray<JournalEntry> replayed = [];
    /// <summary>Entries in the journal, replayed and live; the next delivery is <c>length + 1</c>.</summary>
    private long journalLength;
    /// <summary>Replayed entries delivered so far.</summary>
    private int nextReplayed;
    private bool replaying;
    /// <summary>Whether an earlier attempt ran this call: its nested call keys may already exist.</summary>
    private bool rerun;
    /// <summary>Whether live calls may launch; before, they are held (see <see cref="ReleaseAsync"/>).</summary>
    private bool released;
    /// <summary>The count an earlier attempt recorded after the script's first run.</summary>
    private long? firstIssued;
    /// <summary>The highest issued count known to be committed: calls up to it may launch.</summary>
    private long durableIssued;
    /// <summary>Live calls waiting for their issued count to be committed.</summary>
    private List<CodemodeHostCall> waiting = [];
    /// <summary>Journal index of each replayed call ID.</summary>
    private Dictionary<long, int> replayIndex = [];
    /// <summary>Calls the VM issued so far; its call IDs are 1, 2, ...</summary>
    private long issued;
    /// <summary>Replay: results of journaled calls, ready to deliver in journal order.</summary>
    private readonly Dictionary<long, CodemodeOutcome> ready = [];
    /// <summary>Replay: calls the journal does not have, admitted once replay is done.</summary>
    private List<CodemodeHostCall> held = [];
    /// <summary>Live: finished calls waiting for their journal commit.</summary>
    private List<Finished> queue = [];
    /// <summary>Acknowledged counts to record, by result number (0: the script's first run), in the order first acknowledged.</summary>
    private readonly List<long> ackOrder = [];
    private readonly Dictionary<long, long> acks = [];
    private bool committing;
    /// <summary>Set once the call decided an outcome other than the script's own (divergence, mismatch).</summary>
    private ToolExecutionResult? ended;
    private Exception? failure;
    /// <summary><c>models.classify()</c>/<c>generateImages()</c> calls in flight, and those waiting for a slot.</summary>
    private int modelCalls;
    private readonly Queue<TaskCompletionSource> modelWaiters = new();
    /// <summary>Images <c>models.generateImages()</c> returned, to notice a script that never shows them.</summary>
    private int generatedImages;
    /// <summary>The script's tool calls for <c>details</c>, by VM call ID.</summary>
    private readonly Dictionary<long, Row> rows = [];
    /// <summary>Completes once the VM's fingerprint is checked or recorded; nothing is delivered or admitted before.</summary>
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DurableCodemodeCall(CodemodeToolOptions options, IToolExecutionApi api, DurableContext context)
    {
        this.options = options;
        this.api = api;
        this.context = context;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Runs <paramref name="action"/> on the call's scheduler, after what is queued there.</summary>
    private void Post(Action action) => Task.Factory.StartNew(() =>
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, loop);

    /// <summary>Starts <paramref name="work"/> on the call's scheduler; a failure it does not handle fails the call.</summary>
    private void Spawn(Func<Task> work) => Post(() => _ = Guard(work));

    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    public Task<ToolExecutionResult> ExecuteAsync(string source) =>
        Task.Factory.StartNew(() => ExecuteCoreAsync(source), CancellationToken.None, TaskCreationOptions.DenyChildAttach, loop).Unwrap();

    private async Task<ToolExecutionResult> ExecuteCoreAsync(string source)
    {
        var parsed = CodemodeSource.Parse(source);
        var agent = await api.AgentAsync(context);
        var tools = agent.Callable.Select(tool => new DeclaredTool(tool.Name, CodemodeIdentifier.ToIdentifier(tool.Name), CodemodeDescribe.SampleOf(tool),
            tool.StructuredOutputSchema is not null, tool.Name.StartsWith(CodemodeModels.InternalPrefix, StringComparison.Ordinal))).ToImmutableArray();
        var callable = agent.Callable.Where(tool => !tool.Name.StartsWith(CodemodeModels.InternalPrefix, StringComparison.Ordinal)).ToList();
        IEnumerable<string> models = options.Models == false ? [] : [.. CodemodeModels.CatalogGlobals, .. CodemodeModels.ModelCallGlobals];
        ImmutableArray<(string Name, bool Spread)> globals =
        [
            .. DiscoveryGlobals.Select(name => (name, true)),
            .. models.Select(name => (name, true)),
            .. (options.Globals ?? []).Select(global => (global.Name, global.Spread)),
        ];
        var namespaces = new List<(string Name, string? Description, string? Instructions, List<string> Tools)>();
        foreach (var tool in callable)
        {
            var found = CodemodeDescribe.NamespaceOf(tool.Name, options.Namespaces ?? []);
            if (found is null) continue;
            var index = namespaces.FindIndex(candidate => candidate.Name == found.Name);
            if (index < 0)
            {
                namespaces.Add((found.Name, found.Description, found.Instructions, []));
                index = namespaces.Count - 1;
            }
            namespaces[index].Tools.Add(tool.Name);
        }
        var now = Now();
        var seed = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        var startJson = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("version", (JsonValue)DurableCodemode.ModuleVersion),
            KeyValuePair.Create("startedAt", (JsonValue)now),
            KeyValuePair.Create("seed", (JsonValue)seed),
        };
        if (parsed.Options.TimeoutMs is { } timeout) startJson.Add(KeyValuePair.Create("deadline", (JsonValue)(now + timeout)));
        startJson.Add(KeyValuePair.Create("tools", (JsonValue)JsonValue.Array(tools.Select(ToolJson))));
        startJson.Add(KeyValuePair.Create("namespaces", (JsonValue)JsonValue.Array(namespaces.Select(entry => (JsonValue)JsonValue.Object(
        [
            KeyValuePair.Create("name", (JsonValue)entry.Name),
            .. entry.Description is null ? [] : new[] { KeyValuePair.Create("description", (JsonValue)entry.Description) },
            .. entry.Instructions is null ? [] : new[] { KeyValuePair.Create("instructions", (JsonValue)entry.Instructions) },
            KeyValuePair.Create("tools", (JsonValue)JsonValue.Array(entry.Tools.Select(name => (JsonValue)name))),
        ])))));
        startJson.Add(KeyValuePair.Create("globals", (JsonValue)JsonValue.Array(globals.Select(global => (JsonValue)JsonValue.Object(
        [
            KeyValuePair.Create("name", (JsonValue)global.Name), KeyValuePair.Create("spread", (JsonValue)global.Spread),
        ])))));
        startJson.Add(KeyValuePair.Create("maxOutputTokens", (JsonValue)(double)(parsed.Options.MaxOutputTokens ?? CodemodeOutput.DefaultMaxOutputTokens)));

        var found2 = await api.CommitAsync<(JsonValue? Done, JsonValue? Start, JsonValue? Journal, bool Rerun)>(async tx =>
        {
            var runDoc = await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId);
            if (runDoc.Get("result") is { } result) return (result, null, null, false);
            var isRerun = runDoc.Get("start") is not null;
            if (!isRerun)
            {
                var store = await tx.DocAsync(DurableCodemode.StoreDoc, api.ConversationId);
                runDoc.Set("start", JsonValue.Object([.. startJson, KeyValuePair.Create("store", store.Get("values") ?? JsonObjectValue.Empty)]));
            }
            var journal = await tx.DocAsync(DurableCodemode.JournalDoc, api.TaskId);
            return (null, runDoc.Get("start"), journal.ToValue(), isRerun);
        }, context);
        if (found2.Done is { } done) return ResultFromJson(done);
        start = ReadStart((JsonObjectValue)found2.Start!);
        var journalValue = (JsonObjectValue)found2.Journal!;
        replayed = [.. ((JsonArrayValue)journalValue["entries"]).Select(ReadEntry)];
        replayIndex = [];
        for (var index = 0; index < replayed.Length; index++) replayIndex[replayed[index].Id] = index;
        journalLength = replayed.Length;
        replaying = replayed.Length > 0;
        rerun = found2.Rerun;
        firstIssued = journalValue.TryGetValue("firstIssued", out var first) && first is JsonNumberValue firstNumber ? (long)firstNumber.Value : null;
        // A loop, not spread arguments: a journal can hold more entries than a call takes arguments.
        durableIssued = replayed.Aggregate(firstIssued ?? 0, (max, entry) => Math.Max(max, entry.IssuedAfter ?? 0));

        if (start.Version.Value != DurableCodemode.ModuleVersion)
        {
            return await Decide(ErrorResult(
                $"The script cannot resume: it started under code mode version {start.Version.ToJson()}, this is {DurableCodemode.ModuleVersion}."));
        }
        double? remaining = start.Deadline is { } deadline ? deadline - Now() : null;
        if (remaining is <= 0) return await Decide(ErrorResult("Script timed out: its deadline passed while it was interrupted."));

        using var onAbort = context.CancellationToken.Register(() => Post(AbortFromContext));
        if (context.IsCancellationRequested) AbortFromContext();
        var runOptions = new CodemodeRunOptions
        {
            StartedAt = start.StartedAt,
            Seed = start.Seed,
            Tools = [.. start.Tools.Where(tool => !tool.Hidden).Select(tool => new CodemodeDeclaredTool(tool.Name) { JsName = tool.JsName, Description = tool.Description })],
            Globals = [.. start.Globals.Select(global => new CodemodeDeclaredGlobal(global.Name) { Spread = global.Spread })],
            Store = [.. start.Store.Select(entry => KeyValuePair.Create(entry.Key, entry.Value.ToData()))],
            TimeoutMs = remaining,
            AbortMessage = controllerAborted ? controllerReason : null,
            OnStarted = fingerprint => Spawn(() => CheckFingerprint(fingerprint)),
            OnCall = call => Post(() => OnCall(call)),
            OnIdle = (seq, count) => Post(() => OnIdle(seq, count)),
        };
        run = options.Sandbox.Start(parsed.Code, runOptions, controllerAborted ? new CancellationToken(true) : CancellationToken.None);
        options.OnRunStarted?.Invoke(run);
        var outcome = await run.Result;
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (ended is not null) return ended;
        context.ThrowIfCancellationRequested();
        // Aborted, but not by this call: the host closed the sandbox, for example while shutting down before closing the Harness. That
        // decides nothing about the script: store nothing and end with this invocation, so a later attempt replays it.
        if (!outcome.Ok && outcome.Error?.Kind == CodemodeErrorKind.Aborted && !controllerAborted)
        {
            if (context.HasCancellation && !context.IsCancellationRequested)
            {
                var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (context.CancellationToken.Register(() => aborted.TrySetResult())) await aborted.Task;
            }
            context.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The code mode sandbox was closed while the script ran");
        }
        // A replay that ends where the live run went on is a divergence (design §4.3).
        // Within the replayed part, the first run included: a rerun without journal entries replays that much too.
        if (rerun && outcome.During is { } during && during.Seq <= replayed.Length)
        {
            // The live run went on: it acknowledged that result, or it made a call that later results answer, which the replay never made.
            var continued = (during.Seq == 0 ? replayed.Length > 0 || firstIssued is not null : replayed[(int)during.Seq - 1].IssuedAfter is not null) ||
                replayed.Skip((int)during.Seq).Any(entry => entry.Id > during.Issued);
            if (continued)
            {
                return await Decide(ErrorResult(
                    $"The script took a different path when resumed after a restart: it ended after result {during.Seq}, where it went on before."));
            }
        }
        return await Complete(outcome);
    }

    // ─── start data and journal JSON ────────────────────────────────────────

    private static JsonValue ToolJson(DeclaredTool tool) => JsonValue.Object(
    [
        KeyValuePair.Create("name", (JsonValue)tool.Name),
        KeyValuePair.Create("jsName", (JsonValue)tool.JsName),
        KeyValuePair.Create("description", (JsonValue)tool.Description),
        .. tool.Structured ? new[] { KeyValuePair.Create("structured", (JsonValue)true) } : [],
        .. tool.Hidden ? new[] { KeyValuePair.Create("hidden", (JsonValue)true) } : [],
    ]);

    private static string? OptionalText(JsonObjectValue value, string key) => value.TryGetValue(key, out var found) && found is JsonStringValue text ? text.Value : null;
    private static double? OptionalNumber(JsonObjectValue value, string key) => value.TryGetValue(key, out var found) && found is JsonNumberValue number ? number.Value : null;

    private static StartData ReadStart(JsonObjectValue value) => new(
        (JsonNumberValue)value["version"],
        ((JsonNumberValue)value["startedAt"]).Value,
        (uint)((JsonNumberValue)value["seed"]).Value,
        OptionalText(value, "fingerprint"),
        OptionalNumber(value, "deadline"),
        [.. ((JsonArrayValue)value["tools"]).Cast<JsonObjectValue>().Select(tool => new DeclaredTool(((JsonStringValue)tool["name"]).Value,
            ((JsonStringValue)tool["jsName"]).Value, ((JsonStringValue)tool["description"]).Value,
            tool.TryGetValue("structured", out var structured) && structured is JsonBooleanValue { Value: true },
            tool.TryGetValue("hidden", out var hidden) && hidden is JsonBooleanValue { Value: true }))],
        [.. ((JsonArrayValue)value["namespaces"]).Cast<JsonObjectValue>().Select(entry => new NamespaceEntry(((JsonStringValue)entry["name"]).Value,
            OptionalText(entry, "description"), OptionalText(entry, "instructions"),
            [.. ((JsonArrayValue)entry["tools"]).Select(name => ((JsonStringValue)name).Value)]))],
        [.. ((JsonArrayValue)value["globals"]).Cast<JsonObjectValue>().Select(global => (((JsonStringValue)global["name"]).Value,
            global["spread"] is JsonBooleanValue { Value: true }))],
        (long)((JsonNumberValue)value["maxOutputTokens"]).Value,
        (JsonObjectValue)value["store"]);

    private static JournalEntry ReadEntry(JsonValue json)
    {
        var entry = (JsonObjectValue)json;
        GlobalEntry? global = null;
        if (entry.TryGetValue("global", out var stored) && stored is JsonObjectValue call)
            global = new GlobalEntry(((JsonStringValue)call["name"]).Value, call.TryGetValue("args", out var args) ? args : null, (JsonObjectValue)call["outcome"]);
        return new JournalEntry((long)((JsonNumberValue)entry["id"]).Value, ((JsonNumberValue)entry["at"]).Value,
            OptionalNumber(entry, "issuedAfter") is { } after ? (long)after : null, global);
    }

    /// <summary>A script call's outcome as the journal stores it: <c>{ ok: true, value? }</c> or <c>{ ok: false, message, properties? }</c>.</summary>
    private static JsonObjectValue OutcomeJson(CodemodeOutcome outcome) => outcome.Ok
        ? JsonValue.Object([KeyValuePair.Create("ok", (JsonValue)true), .. outcome.Value is { } value ? new[] { KeyValuePair.Create("value", JsonValue.FromData(value)) } : []])
        : JsonValue.Object([KeyValuePair.Create("ok", (JsonValue)false), KeyValuePair.Create("message", (JsonValue)outcome.Message!),
            .. outcome.Properties is { } properties ? new[] { KeyValuePair.Create("properties", JsonValue.FromData(properties)) } : []]);

    private static CodemodeOutcome OutcomeOf(JsonObjectValue json) => json.TryGetValue("ok", out var ok) && ok is JsonBooleanValue { Value: true }
        ? CodemodeOutcome.Success(json.TryGetValue("value", out var value) ? value.ToData() : null)
        : CodemodeOutcome.Failure(OptionalText(json, "message") ?? "", json.TryGetValue("properties", out var properties) ? properties.ToData() : null);

    private static JsonValue? ToValue(JsonData? data) => data is null ? null : JsonValue.FromData(data);

    private static ToolExecutionResult ErrorResult(string message) => new()
    {
        Output = JsonValue.Array(JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)message)])),
        IsError = true,
    };

    /// <summary>A result as <c>pi.codemode.run</c> stores it: <c>{ output, isError, details }</c>.</summary>
    private static JsonValue ResultJson(ToolExecutionResult result) => JsonValue.Object(
    [
        .. result.Output is { } output ? new[] { KeyValuePair.Create("output", (JsonValue)output) } : [],
        .. result.IsError is { } isError ? new[] { KeyValuePair.Create("isError", (JsonValue)isError) } : [],
        .. result.Details is { } details ? new[] { KeyValuePair.Create("details", details) } : [],
    ]);

    private static ToolExecutionResult ResultFromJson(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        return new ToolExecutionResult
        {
            Output = value.TryGetValue("output", out var output) ? (JsonArrayValue)output : null,
            StructuredOutput = value.TryGetValue("structuredOutput", out var structured) ? structured : null,
            IsError = value.TryGetValue("isError", out var isError) && isError is JsonBooleanValue flag ? flag.Value : null,
            Details = value.TryGetValue("details", out var details) ? details : null,
        };
    }

    // ─── deciding ───────────────────────────────────────────────────────────

    /// <summary>Store an outcome the call decided, and end the VM if it runs; returns the outcome.</summary>
    private async Task<ToolExecutionResult> Decide(ToolExecutionResult decided)
    {
        var result = decided with { Details = DetailsJson(null) };
        await api.CommitAsync(async tx =>
        {
            var runDoc = await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId);
            if (runDoc.Get("result") is null or JsonNullValue) runDoc.Set("result", ResultJson(result));
        }, context);
        return result;
    }

    /// <summary>End the call with <paramref name="message"/>: store it as the outcome, then stop the VM with an error the script cannot
    /// catch.</summary>
    private void Diverge(string message)
    {
        if (ended is not null || failure is not null) return;
        var result = ErrorResult($"The script took a different path when resumed after a restart: {message}");
        ended = result;
        _ = DivergeAsync(result, message);
    }

    private async Task DivergeAsync(ToolExecutionResult result, string message)
    {
        try
        {
            await Decide(result);
        }
        catch (Exception error)
        {
            Fail(error);
            return;
        }
        Abort(message);
    }

    /// <summary>Whether nothing more may happen: the run has its outcome, or the call decided or failed.</summary>
    private bool Stopped => ended is not null || failure is not null || run?.Finished == true;

    private void Fail(Exception error)
    {
        if (failure is not null) return;
        failure = error;
        Abort(DurableErrors.MessageOf(error));
    }

    /// <summary>Upstream's <c>controller.abort(reason)</c>: the first abort wins.</summary>
    private void Abort(string reason)
    {
        if (controllerAborted) return;
        controllerAborted = true;
        controllerReason = reason;
        if (run is { } started) _ = started.AbortAsync(reason);
    }

    private void AbortFromContext() =>
        Abort(context.CancellationReason is Exception reason ? DurableErrors.MessageOf(reason) : "This operation was aborted");

    /// <summary>The script's own outcome, stored with its store writes in one transaction.</summary>
    private async Task<ToolExecutionResult> Complete(CodemodeResult result)
    {
        var calls = SortedRows();
        var formatted = await CodemodeOutput.FormatResult(result, generatedImages, Now() - start.StartedAt, start.MaxOutputTokens,
            [.. calls.Select(row => (row.Name, row.Status == "running" ? "cancelled" : row.Status))], DurableCodemode.OutputLimits, OutputFiles());
        var tool = new ToolExecutionResult { Output = formatted.Content, IsError = !result.Ok, Details = DetailsJson(formatted.FullOutputPath) };
        var writes = result.Ok ? result.StoreWrites : null;
        return await api.CommitAsync(async tx =>
        {
            var runDoc = await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId);
            if (runDoc.Get("result") is { } stored and not JsonNullValue) return ResultFromJson(stored);
            runDoc.Set("result", ResultJson(tool));
            if (writes is not null && (writes.Set.Length > 0 || writes.Delete.Length > 0))
            {
                var store = await tx.DocAsync(DurableCodemode.StoreDoc, api.ConversationId);
                var values = store.GetObject("values");
                foreach (var key in writes.Delete) values.Delete(key);
                foreach (var (key, value) in writes.Set) values.Set(key, JsonValue.FromData(value));
            }
            return tool;
        }, context);
    }

    /// <summary>Writes the result's files under fixed names into the run's output directory, so a rerun overwrites them.</summary>
    private CodemodeOutputFile OutputFiles()
    {
        var env = api.Env;
        Task<string>? directory = null;
        return async (name, data) =>
        {
            if (env is null) return (null, "no execution environment");
            try
            {
                directory ??= OutputDir(env);
                var path = $"{await directory}/{name}";
                var written = await env.WriteFileAsync(path, data, context);
                return written.IsOk ? (path, null) : (null, written.Error.Message);
            }
            catch (Exception error)
            {
                return (null, DurableErrors.MessageOf(error));
            }
        };
    }

    /// <summary>The run's output directory: the recorded one, else a new temp directory, recorded before any file is written.</summary>
    private async Task<string> OutputDir(IExecutionEnv env)
    {
        var recorded = await api.CommitAsync(async tx =>
            (await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId)).Get("outputDir") is JsonStringValue text ? text.Value : null, context);
        if (recorded is not null) return recorded;
        var created = await env.CreateTempDirAsync("pi-codemode-", context);
        if (!created.IsOk) throw new InvalidOperationException(created.Error.Message);
        return await api.CommitAsync(async tx =>
        {
            var runDoc = await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId);
            if (runDoc.Get("outputDir") is null or JsonNullValue) runDoc.Set("outputDir", created.Value);
            return ((JsonStringValue)runDoc.Get("outputDir")!).Value;
        }, context);
    }

    // ─── the sandbox's callbacks ────────────────────────────────────────────

    private async Task CheckFingerprint(string fingerprint)
    {
        if (start.Fingerprint is { } recorded)
        {
            if (recorded != fingerprint)
            {
                ended = ErrorResult($"The script cannot resume: the sandbox changed since it started ({recorded} -> {fingerprint}).");
                try
                {
                    await Decide(ended);
                }
                catch (Exception error)
                {
                    Fail(error);
                }
                Abort("Sandbox fingerprint changed");
                return;
            }
        }
        else
        {
            try
            {
                await api.CommitAsync(async tx =>
                {
                    var runDoc = await tx.DocAsync(DurableCodemode.RunDoc, api.TaskId);
                    if (runDoc.Get("start") is JsonObjectValue) runDoc.GetObject("start").Set("fingerprint", fingerprint);
                }, context);
            }
            catch (Exception error)
            {
                Fail(error);
                return;
            }
        }
        gate.TrySetResult();
        // Nothing was delivered before the gate: start now. A rerun without a journal releases its calls at the script's first acknowledgment
        // instead.
        if (replaying) DeliverReplayed();
        else if (!rerun) _ = ReleaseAsync();
    }

    private void OnCall(CodemodeHostCall made)
    {
        if (Stopped) return;
        var call = AsToolCall(made);
        if (call.Target == CodemodeCallTarget.Tool) StartedRow(call);
        issued = Math.Max(issued, call.Id);
        var index = replaying ? replayIndex.GetValueOrDefault(call.Id, -1) : -1;
        if (index == -1)
        {
            if (released)
            {
                waiting.Add(call);
                LaunchCovered();
            }
            else held.Add(call);
            return;
        }
        var entry = replayed[index];
        if (entry.Global is { } global)
        {
            if (call.Target != CodemodeCallTarget.Global || call.Name != global.Name || !SameJson(ToValue(call.Args), global.Args))
            {
                Diverge($"call {call.Id} is {Describe(call)}, but was a call to {global.Name}");
                return;
            }
            ready[call.Id] = OutcomeOf(global.Outcome);
            _ = AfterGate(DeliverReplayed);
            return;
        }
        if (call.Target != CodemodeCallTarget.Tool)
        {
            Diverge($"call {call.Id} is {Describe(call)}, but was a tool call");
            return;
        }
        _ = ReplayTool(call);
    }

    private async Task AfterGate(Action then)
    {
        await gate.Task;
        then();
    }

    private async Task ReplayTool(CodemodeHostCall call)
    {
        CodemodeOutcome outcome;
        try
        {
            await gate.Task;
            outcome = await CallTool(call);
        }
        catch (Exception error)
        {
            if (Stopped) return;
            if (context.IsCancellationRequested) Fail(error);
            // The key names another tool or other arguments.
            else Diverge($"call {call.Id} is {Describe(call)}: {DurableErrors.MessageOf(error)}");
            return;
        }
        ready[call.Id] = outcome;
        DeliverReplayed();
    }

    /// <summary>Replay: deliver journaled results in journal order, each once the VM has issued its call.</summary>
    private void DeliverReplayed()
    {
        while (nextReplayed < replayed.Length && !Stopped)
        {
            var entry = replayed[nextReplayed];
            if (entry.Id > issued || !ready.TryGetValue(entry.Id, out var outcome)) return;
            ready.Remove(entry.Id);
            nextReplayed++;
            Deliver(nextReplayed, entry.At, entry.Id, outcome);
        }
    }

    /// <summary>Hands the VM a result; a delivery the run refuses (a bug, not a script's doing) fails the call.</summary>
    private void Deliver(long seq, double at, long id, CodemodeOutcome outcome)
    {
        try
        {
            run!.Deliver(seq, at, id, outcome);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            if (!run!.Finished) Fail(error);
        }
    }

    private void OnIdle(long seq, long count)
    {
        if (Stopped) return;
        // A count an earlier attempt recorded must match; a missing one is recorded now.
        if (rerun && seq <= replayed.Length)
        {
            var recorded = seq == 0 ? firstIssued : replayed[(int)seq - 1].IssuedAfter;
            if (recorded is { } expected && expected != count)
            {
                Diverge(seq == 0
                    ? $"its first run made {count} calls, where it had made {expected}"
                    : $"after result {seq} it had made {count} calls, where it had made {expected}");
                return;
            }
        }
        if (!acks.ContainsKey(seq)) ackOrder.Add(seq);
        acks[seq] = count;
        if (!replaying)
        {
            // A rerun without a journal: every call an earlier attempt could have admitted is made by now.
            if (seq == 0 && rerun) _ = ReleaseAsync();
            // Committed at once when the script has processed everything delivered: calls it made wait for it, and a script waiting on a
            // long call is where a restart usually finds it. Otherwise with the next commit; the VM catches up without waiting on anything.
            if (seq == journalLength) _ = Pump();
            return;
        }
        // Caught up: the VM processed everything delivered and waits.
        if (seq != nextReplayed) return;
        if (seq < replayed.Length)
        {
            var next = replayed[(int)seq];
            if (next.Id > count) Diverge($"it waits without making call {next.Id}, which it made before");
            return;
        }
        // The last replayed result is acknowledged: continue live, admitting the calls the journal did not have once their count is
        // committed.
        replaying = false;
        _ = Pump();
        _ = ReleaseAsync();
    }

    /// <summary>
    /// Let the held calls launch, and later live calls, each once its issued count is committed. On a rerun, an earlier attempt may have
    /// admitted some of them under their keys, as other calls: when the replay releases (idle after the last journaled result, or after its
    /// first run without a journal) its counts matched every recorded one, so it has made every call an earlier attempt could have admitted.
    /// All held calls are checked first, whatever their target, and a divergence launches none. Calls made after this are new.
    /// </summary>
    private async Task ReleaseAsync()
    {
        try
        {
            await gate.Task;
            if (Stopped || released) return;
            var calls = held;
            held = [];
            if (rerun)
            {
                foreach (var call in calls)
                {
                    string? other;
                    try
                    {
                        other = await Earlier(call);
                    }
                    catch (Exception error)
                    {
                        Fail(error);
                        return;
                    }
                    if (Stopped) return;
                    if (other is not null)
                    {
                        Diverge($"call {call.Id} is {Describe(call)}, but was {other}");
                        return;
                    }
                }
            }
            released = true;
            waiting.AddRange(calls);
            waiting.AddRange(held);
            held = [];
            LaunchCovered();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>Launch the waiting calls whose issued count is committed.</summary>
    private void LaunchCovered()
    {
        if (!released || Stopped) return;
        var covered = waiting.Where(call => call.Id <= durableIssued).ToList();
        if (covered.Count == 0) return;
        waiting = [.. waiting.Where(call => call.Id > durableIssued)];
        ToolExecutionApi.AdmitTogether(api, () =>
        {
            foreach (var call in covered) Launch(call);
        });
    }

    /// <summary>The call an earlier attempt made under this call's key, when it is another one: another target, tool, or arguments.</summary>
    private async Task<string?> Earlier(CodemodeHostCall call)
    {
        var state = await api.SnapshotAsync(HarnessTasks.NestedCallDoc, api.TaskId, $"c{call.Id}", context);
        if (state is null || !state.TryGetValue("taskId", out var taskId) || taskId is not JsonNumberValue id) return null;
        var input = (await api.GetTaskAsync(new TaskId((long)id.Value), context))?.Input as JsonObjectValue;
        if (input is null || !input.TryGetValue("kind", out var kind) || kind is not JsonStringValue { Value: "nested" }) return null;
        var made = (JsonObjectValue)input["call"];
        var madeName = ((JsonStringValue)made["name"]).Value;
        var madeArguments = made.TryGetValue("arguments", out var arguments) ? arguments : null;
        if (call.Target == CodemodeCallTarget.Tool && madeName == call.Name && SameJson(madeArguments, ToValue(call.Args) ?? JsonObjectValue.Empty)) return null;
        return $"tools.{madeName}({madeArguments?.ToJson()})";
    }

    /// <summary>Run one live call and journal its result.</summary>
    private void Launch(CodemodeHostCall call)
    {
        if (Stopped) return;
        _ = LaunchAsync(call);
    }

    private async Task LaunchAsync(CodemodeHostCall call)
    {
        CodemodeOutcome outcome;
        JsonObjectValue? global = null;
        try
        {
            if (call.Target == CodemodeCallTarget.Tool) outcome = await CallTool(call);
            else
            {
                outcome = await CallGlobal(call);
                global = JsonValue.Object(
                [
                    KeyValuePair.Create("name", (JsonValue)call.Name),
                    .. call.Args is { } args ? new[] { KeyValuePair.Create("args", JsonValue.FromData(args)) } : [],
                    KeyValuePair.Create("outcome", (JsonValue)OutcomeJson(outcome)),
                ]);
            }
        }
        catch (Exception error)
        {
            // Stopped meanwhile: the outcome is decided, or a waiting model call gave up.
            if (Stopped) return;
            if (context.IsCancellationRequested) Fail(error);
            // A key an earlier attempt admitted with another tool or other arguments: this attempt took another path.
            else Diverge($"call {call.Id} is {Describe(call)}: {DurableErrors.MessageOf(error)}");
            return;
        }
        queue.Add(new Finished(call.Id, Now(), outcome, global));
        _ = Pump();
    }

    private async Task<CodemodeOutcome> CallTool(CodemodeHostCall call)
    {
        // Passed as the script gave them; validation reports arguments that are not an object.
        var args = ToValue(call.Args) ?? JsonObjectValue.Empty;
        var model = call.Name.StartsWith(CodemodeModels.InternalPrefix, StringComparison.Ordinal);
        if (model)
        {
            await ModelSlot();
            // The call may have stopped while this one waited: a finished script admits no new paid call.
            if (Stopped)
            {
                ReleaseModelSlot();
                throw new InvalidOperationException("The script has ended");
            }
        }
        NestedToolExecutionResult result;
        try
        {
            result = await ToolExecutionApi.ExecuteToolAsync(api, call.Name, args, context, new NestedCallOptions { Key = $"c{call.Id}" });
        }
        finally
        {
            if (model) ReleaseModelSlot();
        }
        if (call.Name == CodemodeModels.GenerateImagesTool) generatedImages += CountImages(result.StructuredOutput);
        var structured = start.Tools.FirstOrDefault(tool => tool.Name == call.Name)?.Structured == true;
        var outcome = OutcomeOf(call.Name, structured, result);
        FinishRow(call.Id, outcome, result);
        return outcome;
    }

    /// <summary>
    /// What a script gets for a nested call, as in the coding agent. A tool with a schema resolves with its value, also on an error result that
    /// carries one (<c>bash</c>'s <c>{ output, exitCode }</c> for a nonzero exit); a schema-less tool resolves with its text output, images left
    /// out, as its declaration says. Any other error rejects, with its text: a schema-less tool's output, else its error diagnostics.
    /// </summary>
    private static CodemodeOutcome OutcomeOf(string name, bool structured, NestedToolExecutionResult result)
    {
        var value = structured ? result.StructuredOutput : TextOf(result.StructuredOutput);
        if (!result.IsError || (structured && value is not null)) return CodemodeOutcome.Success(value?.ToData());
        var message = structured ? "" : ((JsonStringValue)value!).Value;
        var diagnostics = string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == ToolDiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.Message));
        return CodemodeOutcome.Failure(message.Length > 0 ? message : diagnostics.Length > 0 ? diagnostics : $"Tool {name} failed",
            value is null ? null : JsonValue.Object([KeyValuePair.Create("structuredOutput", value)]).ToData());
    }

    /// <summary>The text of a schema-less tool's collapsed output: a string, one image, or an array of text and image blocks.</summary>
    private static JsonValue TextOf(JsonValue? value) => value switch
    {
        JsonStringValue text => text,
        JsonArrayValue items => string.Join("\n", items.OfType<JsonObjectValue>()
            .Where(item => item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" })
            // Array.prototype.join prints a missing text as "".
            .Select(item => item.TryGetValue("text", out var text) && text is JsonStringValue content ? content.Value : text is null or JsonNullValue ? "" : text.ToJson())),
        _ => "",
    };

    /// <summary>The script made a tool call: its row appears, running, also while it waits to launch.</summary>
    private void StartedRow(CodemodeHostCall call)
    {
        if (rows.ContainsKey(call.Id)) return;
        var args = call.Args is null ? "" : JsonValue.FromData(call.Args).ToJson();
        var name = call.Name.StartsWith(CodemodeModels.InternalPrefix, StringComparison.Ordinal) ? call.Name[CodemodeModels.InternalPrefix.Length..] : call.Name;
        rows[call.Id] = new Row(call.Id, name, JsString.Cut(args, ArgsPreviewChars));
    }

    /// <summary>The call ended: an error result is an error row, also when the script got its value (bash's nonzero exit).</summary>
    private void FinishRow(long id, CodemodeOutcome outcome, NestedToolExecutionResult result)
    {
        if (!rows.TryGetValue(id, out var row)) return;
        row.Status = result.IsError ? "error" : "ok";
        if (result.DurationMs is { } duration) row.DurationMs = duration;
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == ToolDiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message);
        var error = !outcome.Ok ? outcome.Message! : string.Join("\n", errors);
        if (result.IsError && error.Length != 0) row.Error = JsString.Cut(error, ErrorPreviewChars);
    }

    private List<Row> SortedRows() => [.. rows.Values.OrderBy(row => row.Id)];

    /// <summary>The details: the rows in call order, calls still running when the script ended as cancelled.</summary>
    private JsonValue DetailsJson(string? fullOutputPath) => JsonValue.Object(
    [
        KeyValuePair.Create("calls", (JsonValue)JsonValue.Array(SortedRows().Select(row => (JsonValue)JsonValue.Object(
        [
            KeyValuePair.Create("id", (JsonValue)row.Id),
            KeyValuePair.Create("name", (JsonValue)row.Name),
            KeyValuePair.Create("args", (JsonValue)row.Args),
            KeyValuePair.Create("status", (JsonValue)(row.Status == "running" ? "cancelled" : row.Status)),
            .. row.DurationMs is { } duration ? new[] { KeyValuePair.Create("durationMs", (JsonValue)duration) } : [],
            .. row.Error is { } error ? new[] { KeyValuePair.Create("error", (JsonValue)error) } : [],
        ])))),
        .. fullOutputPath is not null ? new[] { KeyValuePair.Create("fullOutputPath", (JsonValue)fullOutputPath) } : [],
    ]);

    private async Task<CodemodeOutcome> CallGlobal(CodemodeHostCall call)
    {
        var discovery = DiscoveryGlobals.Contains(call.Name);
        var catalog = CodemodeModels.CatalogGlobals.Contains(call.Name);
        var global = discovery || catalog ? null : options.Globals?.FirstOrDefault(candidate => candidate.Name == call.Name);
        if (!discovery && !catalog && global is null) return CodemodeOutcome.Failure($"Unknown global {call.Name}");
        try
        {
            // `undefined` stays undefined: the journal omits the value, and a replay resolves with undefined again.
            var value = discovery
                ? Discover(call)
                : catalog
                    ? await CodemodeModels.CatalogCall(call.Name, ToValue(call.Args), api.Models, context)
                    : await global!.Execute(ToValue(call.Args), api, context);
            return CodemodeOutcome.Success(value?.ToData());
        }
        catch (Exception error)
        {
            if (context.IsCancellationRequested) throw;
            return CodemodeOutcome.Failure(DurableErrors.MessageOf(error));
        }
    }

    private async Task ModelSlot()
    {
        if (modelCalls >= CodemodeModels.MaxConcurrentModelCalls)
        {
            var slot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            modelWaiters.Enqueue(slot);
            await slot.Task;
        }
        modelCalls++;
    }

    private void ReleaseModelSlot()
    {
        modelCalls--;
        if (modelWaiters.TryDequeue(out var next)) next.TrySetResult();
    }

    /// <summary><c>searchTools()</c>, <c>describeTool()</c>, <c>describeNamespace()</c>: answered from the start data.</summary>
    private JsonValue? Discover(CodemodeHostCall call)
    {
        var args = ToValue(call.Args) as JsonArrayValue ?? JsonArrayValue.Empty;
        JsonValue? At(int index) => index < args.Count ? args[index] : null;
        var namespaces = start.Namespaces;
        var tools = start.Tools.Where(tool => !tool.Hidden).ToList();
        NamespaceEntry? NamespaceOfTool(string name) => namespaces.FirstOrDefault(entry => entry.Tools.Contains(name));
        static bool MatchesNamespace(NamespaceEntry entry, string query) =>
            entry.Name == query || CodemodeIdentifier.ToIdentifier(entry.Name) == CodemodeIdentifier.ToIdentifier(query);
        if (call.Name == "describeTool")
        {
            if (At(0) is not JsonStringValue name) throw new InvalidOperationException("describeTool() expects a tool name");
            return tools.FirstOrDefault(tool => tool.Name == name.Value || tool.JsName == name.Value)?.Description is { } description ? (JsonValue)description : null;
        }
        if (call.Name == "describeNamespace")
        {
            if (At(0) is not JsonStringValue name) throw new InvalidOperationException("describeNamespace() expects a namespace name");
            var found = namespaces.FirstOrDefault(candidate => MatchesNamespace(candidate, name.Value));
            if (found is null) return null;
            return JsonValue.Object(
            [
                KeyValuePair.Create("name", (JsonValue)found.Name),
                .. found.Description is null ? [] : new[] { KeyValuePair.Create("description", (JsonValue)found.Description) },
                .. found.Instructions is null ? [] : new[] { KeyValuePair.Create("instructions", (JsonValue)found.Instructions) },
                KeyValuePair.Create("tools", (JsonValue)JsonValue.Array(found.Tools.Select(tool => (JsonValue)CodemodeIdentifier.ToIdentifier(tool)))),
            ]);
        }
        if (At(0) is not JsonStringValue query) throw new InvalidOperationException("searchTools() expects a query string");
        var searchOptions = At(1) as JsonObjectValue;
        var limit = searchOptions is not null && searchOptions.TryGetValue("limit", out var given) && given is not JsonNullValue ? given : DefaultSearchLimit;
        if (limit is not JsonNumberValue { Value: var max } || !double.IsFinite(max) || Math.Floor(max) != max || max <= 0)
            throw new InvalidOperationException("searchTools() limit must be a positive integer");
        var inNamespace = searchOptions is not null && searchOptions.TryGetValue("namespace", out var named) ? named : null;
        if (inNamespace is not null and not JsonNullValue and not JsonStringValue) throw new InvalidOperationException("searchTools() namespace must be a string");
        var documents = tools.SelectMany(tool =>
        {
            var own = NamespaceOfTool(tool.Name);
            if (inNamespace is JsonStringValue wanted && (own is null || !MatchesNamespace(own, wanted.Value))) return [];
            string[] text = [tool.Name, tool.Name.Replace('_', ' '), tool.Description, own?.Name ?? "", own?.Description ?? ""];
            return new[] { new SearchDocument(tool.Name, string.Join(" ", text)) };
        }).ToList();
        return JsonValue.Array(Search.RankDocuments(query.Value, documents, max >= int.MaxValue ? int.MaxValue : (int)max).Select(name =>
        {
            var tool = tools.First(candidate => candidate.Name == name);
            return (JsonValue)JsonValue.Object([KeyValuePair.Create("name", (JsonValue)tool.JsName), KeyValuePair.Create("description", (JsonValue)tool.Description)]);
        }));
    }

    /// <summary>
    /// Journal finished calls and acknowledged counts in one commit, then deliver the calls and launch the calls the counts cover; one commit
    /// in flight at a time, so what arrives meanwhile goes into the next.
    /// </summary>
    private async Task Pump()
    {
        if (committing || Stopped) return;
        if (queue.Count == 0 && acks.Count == 0) return;
        committing = true;
        var batch = queue;
        queue = [];
        var counts = ackOrder.Select(seq => (Seq: seq, Issued: acks[seq])).ToList();
        ackOrder.Clear();
        acks.Clear();
        try
        {
            await api.CommitAsync(async tx =>
            {
                var journal = await tx.DocAsync(DurableCodemode.JournalDoc, api.TaskId);
                foreach (var (seq, count) in counts)
                {
                    if (seq == 0)
                    {
                        if (journal.Get("firstIssued") is null or JsonNullValue) journal.Set("firstIssued", count);
                    }
                    else
                    {
                        var entries = journal.GetArray("entries");
                        if (seq - 1 < entries.Count)
                        {
                            var entry = entries.GetObject((int)seq - 1);
                            if (!entry.ContainsKey("issuedAfter")) entry.Set("issuedAfter", count);
                        }
                    }
                }
                foreach (var item in batch)
                {
                    journal.GetArray("entries").Add(JsonValue.Object(
                    [
                        KeyValuePair.Create("id", (JsonValue)item.Id),
                        KeyValuePair.Create("at", (JsonValue)item.At),
                        .. item.Global is { } global ? new[] { KeyValuePair.Create("global", (JsonValue)global) } : [],
                    ]));
                }
            }, context);
        }
        catch (Exception error)
        {
            Fail(error);
            return;
        }
        // An await in JavaScript always lets the reactions already queued run first, so results that finished meanwhile join the next batch;
        // a commit the Session line ran inline completes synchronously here, so yield to them explicitly.
        await Task.Yield();
        foreach (var (_, count) in counts) durableIssued = Math.Max(durableIssued, count);
        foreach (var item in batch)
        {
            journalLength++;
            if (!run!.Finished) Deliver(journalLength, item.At, item.Id, item.Outcome);
        }
        LaunchCovered();
        committing = false;
        _ = Pump();
    }

    /// <summary>A script's call of <c>models.classify()</c> or <c>models.generateImages()</c> as the call of its internal tool.</summary>
    private static CodemodeHostCall AsToolCall(CodemodeHostCall call)
    {
        if (call.Target != CodemodeCallTarget.Global || !CodemodeModels.ModelCalls.TryGetValue(call.Name, out var tool)) return call;
        var list = ToValue(call.Args) as JsonArrayValue ?? JsonArrayValue.Empty;
        JsonValue At(int index) => index < list.Count ? list[index] : JsonValue.Null;
        return new CodemodeHostCall(call.Id, CodemodeCallTarget.Tool, tool,
            JsonValue.Object([KeyValuePair.Create("model", At(0)), KeyValuePair.Create("context", At(1))]).ToData());
    }

    /// <summary>Image blocks in a <c>models.generateImages()</c> result.</summary>
    private static int CountImages(JsonValue? value) =>
        value is JsonObjectValue result && result.TryGetValue("output", out var output) && output is JsonArrayValue blocks
            ? blocks.Count(block => block is JsonObjectValue item && item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "image" })
            : 0;

    private static string Describe(CodemodeHostCall call) =>
        $"{(call.Target == CodemodeCallTarget.Tool ? "tools." : "")}{call.Name}({(call.Args is null ? "" : JsonValue.FromData(call.Args).ToJson())})";

    /// <summary>Structural JSON equality; key order does not matter. Null is undefined.</summary>
    private static bool SameJson(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return (a, b) switch
        {
            (JsonArrayValue left, JsonArrayValue right) => left.Count == right.Count && left.Zip(right).All(pair => SameJson(pair.First, pair.Second)),
            (JsonArrayValue, _) or (_, JsonArrayValue) => false,
            (JsonObjectValue left, JsonObjectValue right) => left.Count == right.Count &&
                left.All(property => right.TryGetValue(property.Key, out var other) && SameJson(property.Value, other)),
            (JsonObjectValue, _) or (_, JsonObjectValue) => false,
            _ => a.ToJson() == b.ToJson(),
        };
    }
}
