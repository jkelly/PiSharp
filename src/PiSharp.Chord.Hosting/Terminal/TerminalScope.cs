// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/tui/scope.ts, packages/chord/src/tui/theme.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Registries;
using PiSharp.Chord.Surfaces;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>A native terminal component's held references, watchers and redraw lifetime.</summary>
public sealed class TerminalScope : IDisposable
{
    private readonly ChordHost _host;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<Action> _release = [];
    private bool _disposed;
    public TerminalScope(ChordHost host, Action changed) { _host = host; _changed = changed; }
    private ChordContext Context => ChordContext.WithAbortSignal(_stop.Token, ChordContext.Background);
    public IReference Hold(Contract contract, JsonObject parameters)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var reference = _host.Hold(contract, parameters, Context);
        Retain(reference.Release); Watch(reference.State); return reference;
    }
    public Func<T?> Watch<T>(IWatchable<T> source)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        _ = FollowAsync(source);
        return () => { try { return source.Value; } catch { return default; } };
    }
    public Func<T?> Watch<T>(INativeWatchable<T> source)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        _ = FollowAsync(source);
        return () => { try { return source.Value; } catch { return default; } };
    }
    private async Task FollowAsync<T>(IWatchable<T> source)
    {
        try
        {
            var watch = await source.WatchAsync(Context).ConfigureAwait(false);
            Retain(() => _ = watch.StopAsync());
            lock (_gate)
            {
                if (_disposed) return;
                watch.Start((_, _) => { Changed(); return Task.CompletedTask; }); Changed();
            }
        }
        catch { /* A disposed or unavailable watch reads as no value until its source recovers. */ }
    }
    private async Task FollowAsync<T>(INativeWatchable<T> source)
    {
        try
        {
            var watch = await source.WatchAsync(Context).ConfigureAwait(false);
            Retain(() => _ = watch.StopAsync());
            lock (_gate)
            {
                if (_disposed) return;
                watch.Start((_, _) => { Changed(); return Task.CompletedTask; }); Changed();
            }
        }
        catch { }
    }
    public Func<T?> Registry<C, T>(Registry<C, T> registry)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        Func<T?> read = () => default;
        _ = Acquire(); return () => read();
        async Task Acquire()
        {
            try { var value = await _host.RegistryAsync(registry, Context).ConfigureAwait(false); read = Watch(value); Changed(); }
            catch { }
        }
    }
    public PageState<T> PageState<T>(string key, T initial)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var state = PageStates.Get(_host, key, initial); Retain(state.Subscribe(Changed)); return state;
    }
    public TerminalTranslator Messages(Messages messages)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var language = ActiveLanguage.HostLanguage(_host); Retain(language.Subscribe(Changed));
        return new(messages, language.Read);
    }
    public TerminalOptimistic<T, A> Optimistic<T, A>(Func<T> read, Func<T, A, T> reduce) => new(read, reduce, Changed);
    public TerminalOptimistic<T, T> Optimistic<T>(Func<T> read) => new(read, (_, action) => action, Changed);
    private void Changed() { lock (_gate) if (!_disposed) _changed(); }
    private void Retain(Action release)
    {
        lock (_gate) { if (!_disposed) { _release.Add(release); return; } }
        release();
    }
    public void Dispose()
    {
        Action[] releases;
        lock (_gate) { if (_disposed) return; _disposed = true; releases = _release.ToArray(); _release.Clear(); }
        _stop.Cancel(); foreach (var release in releases) release(); _stop.Dispose();
    }
}

public sealed class TerminalTranslator(Messages messages, Func<Language?> language)
{
    public string Text(string key, params object?[] arguments) => messages.In(language()).Text(key, arguments);
    public string Render(MessageData message) => messages.In(language()).Render(message);
}

/// <summary>Shows all actions in the current group until every associated call settles, including rejected calls.</summary>
public sealed class TerminalOptimistic<T, A>(Func<T> read, Func<T, A, T> reduce, Action changed)
{
    private readonly object _gate = new();
    private readonly List<A> _actions = [];
    private int _pending, _revision, _cachedRevision = -1;
    private T? _base, _shown;
    public bool Pending { get { lock (_gate) return _pending != 0; } }
    public T Read()
    {
        var value = read();
        lock (_gate)
        {
            if (_pending == 0) return value;
            var same = typeof(T).IsValueType || typeof(T) == typeof(string) ? EqualityComparer<T>.Default.Equals(_base!, value) : ReferenceEquals(_base, value);
            if (_cachedRevision != _revision || !same)
            {
                _base = value; _shown = _actions.Aggregate(value, reduce); _cachedRevision = _revision;
            }
            return _shown!;
        }
    }
    public Task<R> Add<R>(A action, Task<R> call)
    {
        lock (_gate) { _actions.Add(action); _pending++; _revision++; }
        _ = SettleAsync(call); changed(); return call;
    }
    private async Task SettleAsync<R>(Task<R> call)
    {
        try { await call.ConfigureAwait(false); } catch { }
        lock (_gate) { if (--_pending == 0) { _actions.Clear(); _revision++; } }
        changed();
    }
}

public sealed class TerminalTheme(Theme theme, ColorDepth depth)
{
    public Theme Theme { get; private set; } = theme;
    public event Action? Changed;
    public string Foreground(string role, string text) => $"\u001b[{Themes.TerminalColor(Theme, role, depth)}m{text}\u001b[39m";
    public string Background(string role, string text) => $"\u001b[{Themes.TerminalColor(Theme, role, depth, ColorLayer.Background)}m{text}\u001b[49m";
    public void Set(Theme theme) { Theme = theme; Changed?.Invoke(); }
}
