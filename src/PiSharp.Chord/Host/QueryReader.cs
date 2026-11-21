// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/query-reader.ts
//
// Readers of named queries (slice 14 §3.2): what self.LiveQuery, self.Count and self.List have in common. A reader holds the named query
// and its arguments as the factory gave them (a value, or a watchable the arguments follow), opens once the provider is live, and reads
// again when the provider has another live instance or the arguments change: its member shows the previous value until the new read's
// first run is adopted. Mappings:
// - GivenArguments and Opened are closed record hierarchies (upstream's tagged unions).
// - Upstream's Reading is a FramedSource with read, fail and detach. The store's LiveQuery is sealed in another layer, so IReading is an
//   interface a list implements itself and LiveReading implements for a live query.
// - A reader belongs to its host's dispatcher, as the sources it feeds (State/Internal.cs).
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

/// <summary>The arguments a factory gave a reader: a value, or a watchable the arguments follow (upstream <c>GivenArguments</c>).</summary>
internal abstract record GivenArguments
{
    private GivenArguments()
    {
    }

    /// <summary>The arguments themselves.</summary>
    internal sealed record Fixed(JsonNode? Value) : GivenArguments;

    /// <summary>A source or a constant (<see cref="Source"/>, <see cref="Constant"/>) whose value the arguments are.</summary>
    internal sealed record Following(object Source) : GivenArguments;
}

/// <summary>How opening a reader ended (upstream <c>Opened</c>): it has a value; its read has not run yet (a table it reads is being
/// created or migrated: no failure, it runs when the table is ready); it waits on a failure (the provider, its arguments, its first run);
/// or its own code broke.</summary>
internal abstract record Opened
{
    private Opened()
    {
    }

    internal sealed record Ready : Opened;

    internal sealed record Loading : Opened;

    internal sealed record Waiting(Exception Error) : Opened;

    internal sealed record Broken(Exception Error) : Opened;

    internal static readonly Opened IsReady = new Ready();
    internal static readonly Opened IsLoading = new Loading();
}

/// <summary>What a reader reads through and feeds: a live query, a count, a list (upstream <c>Reading</c>).</summary>
internal interface IReading
{
    /// <summary>The source that has the reader's value: its attempt follows it while it waits.</summary>
    FramedSource Source { get; }

    /// <summary>Reads <paramref name="target"/> from now on; completes once its first run settled (adopted, overtaken or held).</summary>
    Task ReadAsync(LiveTarget target, ChordContext context);

    /// <summary>What it should read cannot be read: the source fails, and nothing read before may heal it.</summary>
    void Fail(Exception error, ChordContext context);

    /// <summary>Its attempt ended.</summary>
    void Detach(Exception error);
}

/// <summary>A live query or a count as a reader's reading.</summary>
internal sealed class LiveReading(LiveQuery live) : IReading
{
    public FramedSource Source => live;

    public Task ReadAsync(LiveTarget target, ChordContext context) => live.ReadAsync(target, context);

    public void Fail(Exception error, ChordContext context) => live.Fail(error, context);

    public void Detach(Exception error) => live.Detach(error);
}

/// <summary>What the park step of an attempt (or of an admission) opens and follows: a reader as <c>OpenReadersAsync</c> sees it.</summary>
internal interface IQueryReader
{
    /// <summary>The source that changes when the reader gets a value.</summary>
    FramedSource Source { get; }

    /// <summary>Opens the read and completes once its first run settled; called again while its attempt waits on it.</summary>
    Task<Opened> OpenAsync(ChordContext opening);
}

/// <summary>One reader of an attempt: opened before the attempt goes live, closed at its end. It opens its reading once the provider is
/// live, and reads again whenever what it reads moves (upstream <c>Reader</c>).</summary>
internal sealed class Reader(GivenQuery given, IReading reading, Action wake) : IQueryReader
{
    // The context its reads run under: its attempt's, set by its first open.
    private ChordContext? context;
    // Whether a read was opened: from then on a move reads again, before it only wakes the attempt (wake).
    private bool started;
    private bool closed;

    /// <inheritdoc/>
    public FramedSource Source => reading.Source;

    /// <summary>Opens the read (the provider is live: its attempt's dependencies are) and completes once its first run settled. Called
    /// again while its attempt waits on it: a reader with a value is ready, one that never read tries again, one whose run failed waits
    /// for its next good run.</summary>
    public async Task<Opened> OpenAsync(ChordContext opening)
    {
        if (context is null)
        {
            context = opening;
            given.Follow(Moved);
        }
        if (!started)
        {
            LiveTarget target;
            try { target = given.Target(opening); }
            // Refused arguments and a throwing builder are the reader's own code (invalid, a plain error); a provider or arguments not ready
            // yet are waited for.
            catch (Exception error) { return IsOwnCode(error) ? new Opened.Broken(error) : new Opened.Waiting(error); }
            started = true;
            await reading.ReadAsync(target, opening);
        }
        var source = reading.Source;
        if (source.Ready) return Opened.IsReady;
        return source.Failure is { } failure ? new Opened.Waiting(failure) : Opened.IsLoading;
    }

    /// <summary>Its attempt ended: no commit runs its reads again, its member's reads throw <paramref name="error"/>.</summary>
    internal void Close(Exception error)
    {
        closed = true;
        given.Release();
        reading.Detach(error);
    }

    // What it reads moved: the new read replaces the current one once its first run is adopted. Before its first read its attempt waits
    // on it and tries again.
    private void Moved()
    {
        if (closed) return;
        if (!started)
        {
            wake();
            return;
        }
        LiveTarget target;
        try { target = given.Target(context!); }
        catch (Exception error)
        {
            reading.Fail(error, context!);
            return;
        }
        // A failure of the run is the member's (a failed member with its last value); nothing awaits it.
        _ = reading.ReadAsync(target, context!);
    }

    // Whether the error is of the reader's own code: arguments the schema refused, or a builder that threw.
    private static bool IsOwnCode(Exception error) => error is not ChordError chord || chord.Code == "invalid";
}

/// <summary>A named query with the arguments a factory gave it: what it reads now (<see cref="Target"/>), and a call whenever that
/// changes (another live instance of the provider, other arguments by canonical JSON), made after the batch that changed it (upstream
/// <c>GivenQuery</c>).</summary>
internal sealed class GivenQuery(NamedQueryHandle handle, GivenArguments args, Batch batch) : IDependent
{
    // The canonical JSON of the arguments read last; null before the first read and after a failed one.
    private string? read;
    private Action? moved;
    private Action? release;
    // Set from an invalidation until the batch's flush compares the arguments.
    private bool checking;

    internal NamedQueryHandle Handle => handle;

    /// <summary>What reading the query reads now: the provider's declaration for the arguments' value. Throws while the provider is not
    /// live or the arguments have no value; arguments the provider refuses throw its <c>invalid/args</c>.</summary>
    internal LiveTarget Target(ChordContext context)
    {
        read = null;
        var value = Value();
        var target = handle.Provided().Target(value, context);
        read = DataInternal.CanonicalJson(value, context);
        return target;
    }

    /// <summary>Calls <paramref name="changed"/> from now on whenever <see cref="Target"/> would read something else, until
    /// <see cref="Release"/>.</summary>
    internal void Follow(Action changed)
    {
        moved = changed;
        var releaseInstance = handle.Follow(changed);
        // A constant never changes: only a source is followed.
        var source = args is GivenArguments.Following { Source: Source following } ? following : null;
        source?.Dependents.Add(this);
        release = () =>
        {
            releaseInstance();
            source?.Dependents.Remove(this);
        };
    }

    internal void Release()
    {
        release?.Invoke();
        release = null;
        moved = null;
    }

    /// <summary>The arguments' source changed in the current batch: compared once the batch delivered (no compute here).</summary>
    public void Invalidate()
    {
        if (checking) return;
        checking = true;
        batch.Flushing(context =>
        {
            checking = false;
            string? now;
            try { now = DataInternal.CanonicalJson(Value(), context); }
            // Arguments that cannot be computed: the reader tries to read and shows that failure.
            catch { now = null; }
            if (now is null || now != read) moved?.Invoke();
        });
    }

    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
    }

    // The arguments' value. A reader opens before its attempt's natives, followed sources and pinned keys (an attempt waiting on a read
    // holds none): arguments computed from one have no value then and would get none while the attempt waits. That is the reader's own
    // code, refused as such, not waited for.
    private JsonNode? Value()
    {
        if (args is GivenArguments.Fixed given) return given.Value;
        try
        {
            return ((GivenArguments.Following)args).Source switch { Source source => source.Value, Constant constant => constant.Value, _ => null };
        }
        catch (ChordError error) when (error.Code == "not_ready")
        {
            const string rule = "a reader's arguments read references, registries, writables and earlier reads";
            throw DataInternal.Invalid("args", $"query \"{handle.Label}\": {rule}, not a followed source or a native ({error.Message})");
        }
    }
}
