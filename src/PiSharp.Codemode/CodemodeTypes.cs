// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/types.ts.
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
    ImmutableArray<CodemodeCall> Calls, CodemodeStoreWrites StoreWrites, CodemodeError? Error);

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
    /// <summary>Maximum JavaScript call depth. Default 10000.</summary>
    public int RecursionLimit { get; init; } = CodemodeLimits.DefaultRecursionLimit;
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
