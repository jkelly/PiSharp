// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/types.ts.
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Codemode;

/// <summary>A tool or global's implementation. <paramref name="arguments"/> is whatever the script passed after a JSON round
/// trip (null for <c>undefined</c>); the result must be JSON (null for <c>undefined</c>). A thrown exception surfaces in the
/// script as an <c>Error</c> with the same message. <paramref name="cancellationToken"/> is cancelled when the script finishes
/// (including unawaited calls), times out, is aborted or the sandbox closes.</summary>
public delegate ValueTask<JsonData?> CodemodeToolExecute(JsonData? arguments, CancellationToken cancellationToken);

/// <summary>types.ts <c>CodemodeTool</c>. Tools are called as <c>tools.&lt;id&gt;(args)</c> and <c>tools["&lt;name&gt;"](args)</c>;
/// globals as <c>&lt;name&gt;(args)</c> or <c>&lt;namespace&gt;.&lt;member&gt;(args)</c>.</summary>
public sealed record CodemodeTool(string Name, CodemodeToolExecute? Execute = null)
{
    /// <summary>Shown as a doc comment in declarations, and listed in <c>ALL_TOOLS</c> for tools.</summary>
    public string? Description { get; init; }
    /// <summary>Schema of the single argument; <c>unknown</c> when omitted.</summary>
    public JsonData? InputSchema { get; init; }
    /// <summary>Schema of the resolved value; <c>unknown</c> when omitted.</summary>
    public JsonData? OutputSchema { get; init; }
    /// <summary>Globals only: the implementation receives all call arguments as an array.</summary>
    public bool Spread { get; init; }
    /// <summary>Globals only: TypeScript parameter list and return type, replacing the rendering from the schemas.</summary>
    public string? Signature { get; init; }
}

/// <summary>One output item, in the order the script produced it. Image data is base64.</summary>
public abstract record CodemodeOutputItem;
/// <summary><paramref name="Console"/> marks <c>console.*</c> output.</summary>
public sealed record CodemodeTextOutput(string Text, bool Console = false) : CodemodeOutputItem;
public sealed record CodemodeImageOutput(string Data, string MimeType) : CodemodeOutputItem;

public enum CodemodeCallStatus { Ok, Error, Cancelled }

public sealed record CodemodeCall(string Name, CodemodeCallStatus Status, double DurationMs);

/// <summary>Script: the script threw or failed to parse. Timeout: the deadline expired. Aborted: the caller's token fired or the
/// sandbox closed. Sandbox: the engine failed outside the script's control.</summary>
public enum CodemodeErrorKind { Script, Timeout, Aborted, Sandbox }

public sealed record CodemodeError(CodemodeErrorKind Kind, string Message, string? Name = null, string? Stack = null);

/// <summary>Keys the script changed with <c>store()</c>; deletions are keys stored as <c>undefined</c>.</summary>
public sealed record CodemodeStoreWrites(ImmutableArray<KeyValuePair<string, JsonData>> Set, ImmutableArray<string> Delete)
{
    public static CodemodeStoreWrites Empty { get; } = new([], []);
    public bool IsEmpty => Set.IsEmpty && Delete.IsEmpty;
}

/// <summary>Output is kept for failed executions too, up to the failure. <c>exit()</c> completes with no value.</summary>
public sealed record CodemodeResult(bool Ok, JsonData? Value, ImmutableArray<CodemodeOutputItem> Output,
    ImmutableArray<CodemodeCall> Calls, CodemodeStoreWrites StoreWrites, CodemodeError? Error)
{
    /// <summary>In a <see cref="CodemodeSandbox.Start"/> run that the script itself ended (it returned, threw, or exited), the result
    /// being processed when it did. Null for <see cref="CodemodeSandbox.ExecuteAsync"/>.</summary>
    public CodemodeDuring? During { get; init; }
}

/// <summary>types.ts <c>CodemodeDuring</c>: in a <see cref="CodemodeSandbox.Start"/> run, the result the engine was processing when the
/// script finished (0 for its first run) and the calls issued by then.</summary>
public sealed record CodemodeDuring(long Seq, long Issued);

/// <summary>Whether a call went to <c>tools.&lt;name&gt;</c> or to a global.</summary>
public enum CodemodeCallTarget { Tool, Global }

/// <summary>types.ts <c>CodemodeHostCall</c>: a call the script made in a <see cref="CodemodeSandbox.Start"/> run, answered with
/// <see cref="CodemodeRun.Deliver"/>. <paramref name="Id"/> is the engine's own number for the call, 1, 2, ... in the order the script
/// made its calls (tools and globals). <paramref name="Args"/> is what the script passed after a JSON round trip (null for
/// <c>undefined</c>); for spread globals the argument list.</summary>
public sealed record CodemodeHostCall(long Id, CodemodeCallTarget Target, string Name, JsonData? Args);

/// <summary>types.ts <c>CodemodeDeclaredTool</c>: a tool as a <see cref="CodemodeSandbox.Start"/> run's script sees it,
/// <c>tools.&lt;JsName&gt;</c> and its <c>ALL_TOOLS</c> entry.</summary>
public sealed record CodemodeDeclaredTool(string Name)
{
    /// <summary>The identifier the script uses. Default: <see cref="CodemodeIdentifier.ToIdentifier"/> of the name. A host that replays a
    /// run passes the one the run started with, so a changed mapping cannot rename a tool under a resumed script.</summary>
    public string? JsName { get; init; }
    public string? Description { get; init; }
}

/// <summary>types.ts <c>CodemodeDeclaredGlobal</c>: a global as a <see cref="CodemodeSandbox.Start"/> run's script sees it; the same
/// names as <see cref="CodemodeSandboxOptions.Globals"/> allow.</summary>
public sealed record CodemodeDeclaredGlobal(string Name)
{
    public bool Spread { get; init; }
}

/// <summary>types.ts <c>CodemodeOutcome</c>: a call's answer. A failure becomes an <c>Error</c> with <see cref="Message"/> in the script,
/// carrying the own properties of <see cref="Properties"/> (a JSON object; <c>name</c>, <c>message</c> and <c>stack</c> are ignored),
/// for example <c>{ structuredOutput }</c>.</summary>
public sealed record CodemodeOutcome
{
    private CodemodeOutcome(bool ok, JsonData? value, string? message, JsonData? properties)
    { Ok = ok; Value = value; Message = message; Properties = properties; }

    public bool Ok { get; }
    /// <summary>The resolved value when <see cref="Ok"/>; null for <c>undefined</c>.</summary>
    public JsonData? Value { get; }
    /// <summary>The rejection's message when not <see cref="Ok"/>.</summary>
    public string? Message { get; }
    public JsonData? Properties { get; }

    /// <summary><c>{ ok: true, value }</c>.</summary>
    public static CodemodeOutcome Success(JsonData? value) => new(true, value, null, null);
    /// <summary><c>{ ok: false, message, properties? }</c>.</summary>
    public static CodemodeOutcome Failure(string message, JsonData? properties = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(false, null, message, properties);
    }
}

/// <summary>types.ts <c>CodemodeRunOptions</c>.</summary>
public sealed record CodemodeRunOptions
{
    /// <summary>The engine clock's start, in ms since the epoch: <c>Date.now()</c> until the first result arrives.</summary>
    public required double StartedAt { get; init; }
    /// <summary>Seeds <c>Math.random()</c>.</summary>
    public required uint Seed { get; init; }
    public ImmutableArray<CodemodeDeclaredTool> Tools { get; init; } = [];
    public ImmutableArray<CodemodeDeclaredGlobal> Globals { get; init; } = [];
    /// <summary>Values the script reads with <c>load(key)</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonData>>? Store { get; init; }
    /// <summary>Deadline of this run in ms. Default: none (not the sandbox's default).</summary>
    public double? TimeoutMs { get; init; }
    /// <summary>Message of a run the caller's token aborts (an AbortSignal's reason); default "This operation was aborted".</summary>
    public string? AbortMessage { get; init; }
    /// <summary>The engine's fingerprint, before the script runs and before any of its calls.</summary>
    public Action<string>? OnStarted { get; init; }
    /// <summary>The script made a call. Answer it with <see cref="CodemodeRun.Deliver"/>, now or later.</summary>
    public required Action<CodemodeHostCall> OnCall { get; init; }
    /// <summary>The engine processed result <c>seq</c> (0: the script's first run, before any result) and waits again, having issued
    /// <c>issued</c> calls in total.</summary>
    public Action<long, long>? OnIdle { get; init; }
}

public sealed record CodemodeSandboxOptions
{
    public ImmutableArray<CodemodeTool> Tools { get; init; } = [];
    /// <summary>Functions exposed as top-level identifiers (or <c>ns.member</c>) instead of on <c>tools</c>; not recorded in calls.</summary>
    public ImmutableArray<CodemodeTool> Globals { get; init; } = [];
    /// <summary>Overall deadline per execution, including time in tools. Null disables it. Default 300000 ms.</summary>
    public double? TimeoutMs { get; init; } = 300_000;
    /// <summary>Allocation budget of one uninterrupted engine run (between two host entries), in bytes. Default 256 MiB.</summary>
    public long MemoryLimitBytes { get; init; } = CodemodeLimits.DefaultMemoryLimitBytes;
    /// <summary>Allocation budget of the whole execution, in bytes. Default 1 GiB.</summary>
    public long TotalAllocationLimitBytes { get; init; } = CodemodeLimits.DefaultTotalAllocationLimitBytes;
    /// <summary>How each script's worker process starts (host.ts: a worker per execution). Defaults to
    /// <see cref="CodemodeWorker.Default"/>; null runs the engine on a thread of this process, which deep native recursion
    /// can crash.</summary>
    public CodemodeWorkerLauncher? Worker { get; init; } = CodemodeWorker.Default;
    /// <summary>Replaces the engine with another worker (tests).</summary>
    internal Func<ICodemodeWorker>? WorkerFactory { get; init; }
    /// <summary>Maximum JavaScript call depth. Default 10000.</summary>
    public int RecursionLimit { get; init; } = CodemodeLimits.DefaultRecursionLimit;
    /// <summary>inline.ts <c>interruptBudget</c>: Jint statements one execution may run in total before it fails with
    /// <see cref="CodemodeErrorKind.Timeout"/>. Unlike the deadline it does not depend on the machine's speed, so a replayed
    /// <see cref="CodemodeSandbox.Start"/> run stops at the same point; it is part of the fingerprint. Default: none.</summary>
    public long? InterruptBudget { get; init; }
}

public sealed record CodemodeExecuteOptions
{
    /// <summary>Overrides the sandbox deadline for this execution; <see cref="double.PositiveInfinity"/> disables it.</summary>
    public double? TimeoutMs { get; init; }
    /// <summary>Values the script reads with <c>load(key)</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonData>>? Store { get; init; }
    /// <summary>Message of an execution the caller's token aborts (an AbortSignal's reason); default "This operation was aborted".</summary>
    public string? AbortMessage { get; init; }
}

public static class CodemodeLimits
{
    /// <summary>prelude-source.ts MAX_STORE_VALUE_CHARS.</summary>
    public const int MaxStoreValueChars = 256 * 1024;
    /// <summary>prelude-source.ts MAX_STORE_TOTAL_CHARS.</summary>
    public const int MaxStoreTotalChars = 1024 * 1024;
    /// <summary>prelude-source.ts MAX_OUTPUT_CHARS.</summary>
    public const int MaxOutputChars = 16 * 1024 * 1024;
    /// <summary>prelude-source.ts MAX_OUTPUT_ITEMS.</summary>
    public const int MaxOutputItems = 100_000;
    /// <summary>execute.ts CODEMODE_MEMORY_LIMIT_BYTES (decision 0003).</summary>
    public const long DefaultMemoryLimitBytes = 256L * 1024 * 1024;
    public const long DefaultTotalAllocationLimitBytes = 1024L * 1024 * 1024;
    public const int DefaultRecursionLimit = 10_000;
    public const int DefaultTimeoutMs = 300_000;
}
