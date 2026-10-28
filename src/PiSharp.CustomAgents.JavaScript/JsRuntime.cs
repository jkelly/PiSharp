// Decision 0012: where JavaScript runs. A JsRuntime owns one PiSharp.Codemode sandbox with tighter limits than code mode's defaults (10 s,
// 64 MB live, 256 MB allocated, recursion 2000). Scripts are async function bodies; their inputs are bound as constants on one line ahead of
// the code (`const args = {...};`), so the script's own line numbers are off by exactly one. Isolation: a worker process when the host set a
// launcher (JsWorkerHost.Configure, or JsRuntimeOptions.Worker), else engines run on threads of this process; model-written code requires the
// worker (JsAuthoring refuses to start without one unless the host opts out).
using System.Text;
using Acornima;
using Acornima.Ast;
using PiSharp.Codemode;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.JavaScript;

/// <summary>Where scripts run.</summary>
public enum JsIsolation
{
    /// <summary>A worker process when the host configured one (<see cref="CodemodeWorker.Default"/>), else in process.</summary>
    Default,
    /// <summary>On a thread of this process. A script that nests values very deeply can take the process down.</summary>
    InProcess,
    /// <summary>In a worker process; fails to construct when no launcher is known.</summary>
    Worker,
}

/// <summary>Limits and isolation of a <see cref="JsRuntime"/>.</summary>
public sealed record JsRuntimeOptions
{
    /// <summary>The longest one script may run; default 10 s.</summary>
    public int TimeoutMs { get; init; } = 10_000;
    /// <summary>The most memory a script may hold; default 64 MB.</summary>
    public long MemoryLimitBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>The most memory a script may allocate in total; default 256 MB.</summary>
    public long TotalAllocationLimitBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>The deepest call stack; default 2000.</summary>
    public int RecursionLimit { get; init; } = 2000;
    /// <summary>Where scripts run; default <see cref="JsIsolation.Default"/>.</summary>
    public JsIsolation Isolation { get; init; } = JsIsolation.Default;
    /// <summary>The worker launcher; default <see cref="CodemodeWorker.Default"/>.</summary>
    public CodemodeWorkerLauncher? Worker { get; init; }
}

/// <summary>What one script run produced.</summary>
/// <param name="Ok">Whether the script returned (rather than threw, timed out, or broke a limit).</param>
/// <param name="Value">The returned value as JSON; null when it returned nothing (undefined).</param>
/// <param name="Output">What it printed (<c>console.log</c>, <c>text()</c>), one item per line.</param>
/// <param name="Error">Why it failed.</param>
public sealed record JsResult(bool Ok, JsonValue? Value, string Output, string? Error)
{
    /// <summary>The value as text: a string as is, anything else as JSON; empty when there is none.</summary>
    public string ValueText => Value switch
    {
        null => "",
        JsonStringValue text => text.Value,
        var other => other.ToJson(),
    };
}

/// <summary>Runs JavaScript in a sandbox: no file system, network or host objects, only the values a run binds.</summary>
public sealed class JsRuntime : IAsyncDisposable
{
    private static readonly Lazy<JsRuntime> shared = new(() => new JsRuntime());

    /// <summary>A runtime with the default options, for tools and actions that name none.</summary>
    public static JsRuntime Shared => shared.Value;

    /// <summary>Creates a runtime.</summary>
    public JsRuntime(JsRuntimeOptions? options = null)
    {
        Options = options ?? new JsRuntimeOptions();
        if (Options.TimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(options), "TimeoutMs must be positive.");
        var worker = Options.Isolation switch
        {
            JsIsolation.InProcess => null,
            JsIsolation.Worker => Options.Worker ?? CodemodeWorker.Default
                ?? throw new InvalidOperationException("Worker isolation needs a launcher: call JsWorkerHost.Configure() at startup, or set JsRuntimeOptions.Worker."),
            _ => Options.Worker ?? CodemodeWorker.Default,
        };
        IsIsolated = worker is not null;
        Sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            TimeoutMs = Options.TimeoutMs,
            MemoryLimitBytes = Options.MemoryLimitBytes,
            TotalAllocationLimitBytes = Options.TotalAllocationLimitBytes,
            RecursionLimit = Options.RecursionLimit,
            Worker = worker,
        });
    }

    /// <summary>The options.</summary>
    public JsRuntimeOptions Options { get; }
    /// <summary>Whether scripts run in a worker process.</summary>
    public bool IsIsolated { get; }
    internal CodemodeSandbox Sandbox { get; }

    /// <summary>Runs <paramref name="code"/> (an async function body: <c>return</c> and <c>await</c> work) with <paramref name="bindings"/> as
    /// constants. Never throws for script failures; those come back as a failed result.</summary>
    public async Task<JsResult> RunAsync(string code, IEnumerable<KeyValuePair<string, JsonValue>>? bindings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var result = await Sandbox.ExecuteAsync(Bind(bindings) + code, null, cancellationToken).ConfigureAwait(false);
        var output = string.Join("\n", result.Output.OfType<CodemodeTextOutput>().Select(item => item.Text));
        if (!result.Ok)
            return new JsResult(false, null, output, result.Error is { } error ? Describe(error) : "The script failed.");
        return new JsResult(true, result.Value is { } value ? JsonValue.FromData(value) : null, output, null);
    }

    /// <summary>Why <paramref name="code"/> would not parse as a script body; null when it parses.</summary>
    public static string? CheckSyntax(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        try
        {
            // The sandbox wraps a script the same way; anything that closes the function early parses as more than one expression.
            var expression = new Parser().ParseExpression("(async (tools, console) => {" + code + "\n})");
            return expression is ArrowFunctionExpression ? null : "The code must be the body of one function; it closes the function early.";
        }
        catch (ParseErrorException error)
        {
            return error.Message;
        }
    }

    /// <summary>The line of constants ahead of a script; names are identifiers and values are JSON (also JavaScript).</summary>
    internal static string Bind(IEnumerable<KeyValuePair<string, JsonValue>>? bindings)
    {
        if (bindings is null) return "";
        var line = new StringBuilder();
        foreach (var (name, value) in bindings)
        {
            if (!IsIdentifier(name)) throw new ArgumentException($"'{name}' is not a JavaScript identifier.", nameof(bindings));
            line.Append("const ").Append(name).Append(" = ").Append(value.ToJson()).Append("; ");
        }
        return line.Length == 0 ? "" : line.Append('\n').ToString();
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');

    private static string Describe(CodemodeError error) => error.Kind switch
    {
        CodemodeErrorKind.Timeout => "The script timed out: " + error.Message,
        CodemodeErrorKind.Aborted => "The script was stopped: " + error.Message,
        CodemodeErrorKind.Sandbox => "The sandbox failed: " + error.Message,
        _ => (error.Name is { } name ? name + ": " : "") + error.Message,
    };

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Sandbox.DisposeAsync();
}
