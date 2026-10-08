using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

// Authored evidence support only; never compiled or executed under the current hold.
internal sealed class TestEvidence
{
    private readonly object _gate = new();
    private readonly List<object> _records = [];
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, IdentityBox> _identities = new();
    private long _sequence; private int _identity;
    private sealed record IdentityBox(int Value);

    internal int Identity(object value)
    { lock (_gate) return _identities.GetValue(value, _ => new(++_identity)).Value; }
    internal void Record(string kind, object? value)
    { lock (_gate) _records.Add(new { sequence = ++_sequence, timestamp = Stopwatch.GetTimestamp(), kind, value }); }
    internal void Completed(string criterion)
    { lock (_gate) { _started.Add(criterion); _completed.Add(criterion); } }
    internal void Started(string criterion)
    { lock (_gate) { _started.Add(criterion); _completed.Remove(criterion); } }
    internal string[] Unexecuted(IEnumerable<string> required)
    { lock (_gate) return required.Where(x => !_started.Contains(x)).ToArray(); }
    internal string[] Incomplete(IEnumerable<string> required)
    { lock (_gate) return required.Where(x => _started.Contains(x) && !_completed.Contains(x)).ToArray(); }
    internal bool HasUnjoinedOwner { get; private set; }
    internal void UnjoinedOwner(string owner)
    { HasUnjoinedOwner = true; Record("unjoined-owned-operation; stop batch", owner); }
    internal object[] Records()
    { lock (_gate) return _records.ToArray(); }

    internal void Model(string kind, ModelDescriptor model)
        => Record(kind, new { identity = Identity(model), model.Id, model.Api, model.Provider });
    internal void Snapshot(string kind, CompletionsSourceSnapshot snapshot)
        => Record(kind, new { rawJson = snapshot.Raw.ToString(), snapshot.SerializedJson, snapshot.OwnUndefinedPaths,
            numberBits = NumberBits(snapshot.Raw.Value.GetProperty("value")) });
    internal void Value(string kind, JsonData value, ImmutableArray<string> undefined)
        => Record(kind, new { rawJson = value.ToString(), ownUndefinedPaths = undefined, numberBits = NumberBits(value.Value) });
    internal static object[] NumberBits(JsonElement value)
    {
        var result = new List<object>();
        void Scan(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Number)
                result.Add(new { path, hex = unchecked((ulong)BitConverter.DoubleToInt64Bits(element.GetDouble())).ToString("x16") });
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var p in element.EnumerateObject()) Scan(p.Value, path + "/" + p.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            else if (element.ValueKind == JsonValueKind.Array)
            { var index = 0; foreach (var item in element.EnumerateArray()) Scan(item, path + "/" + index++); }
        }
        Scan(value, ""); return result.ToArray();
    }
}

internal sealed class HeldCallbackBarrier(TestEvidence evidence, string name)
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _release;
    internal Task Entered => _entered.Task;
    internal bool IsReleased => _released.Task.IsCompleted;
    internal async ValueTask BlockAsync(CancellationToken token)
    {
        evidence.Record("callback-held-enter", new { name }); _entered.TrySetResult();
        try { await _released.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { evidence.Record("callback-held-exit", new { name, released = IsReleased, cancelled = token.IsCancellationRequested }); }
    }
    internal void Release()
    { if (Interlocked.Exchange(ref _release, 1) == 0) { evidence.Record("callback-controller-release", new { name }); _released.TrySetResult(); } }
}

internal sealed class ComparisonFailure(string criterion, object? actual, object? expected) : Exception(criterion)
{
    public object? Actual { get; } = actual;
    public object? Expected { get; } = expected;
}
