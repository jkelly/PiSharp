// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/page-state.ts.
using System.Runtime.CompilerServices;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

/// <summary>An in-process value shared across client plugin generations, never sent over a link.</summary>
public sealed class PageState<T>
{
    private readonly object gate = new();
    private T value;
    private readonly HashSet<Action> subscribers = [];
    private bool closed;
    internal PageState(T initial) => value = initial;
    public T Read() { lock (gate) return value; }
    public void Set(T next)
    {
        Action[] changed;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (Same(value, next)) return;
            value = next; changed = [.. subscribers];
        }
        foreach (var callback in changed) callback();
    }
    public Action Subscribe(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); subscribers.Add(changed); }
        return () => { lock (gate) subscribers.Remove(changed); };
    }
    internal void Close() { lock (gate) { closed = true; subscribers.Clear(); value = default!; } }
    private static bool Same(T left, T right) => typeof(T).IsValueType || typeof(T) == typeof(string)
        ? EqualityComparer<T>.Default.Equals(left, right) : ReferenceEquals(left, right);
}

public static class PageStates
{
    private static readonly ConditionalWeakTable<ChordHost, HostStates> Hosts = new();
    public static PageState<T> Get<T>(ChordHost host, string key, T initial)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(key);
        return host.Dispatcher.Invoke(() =>
        {
            host.AssertOpen();
            var states = Hosts.GetValue(host, current =>
            {
                var created = new HostStates();
                current.Closing += created.Close;
                return created;
            });
            if (states.Values.TryGetValue(key, out var value))
                return value is PageState<T> typed ? typed : throw new InvalidOperationException($"Page state '{key}' has a different type");
            var made = new PageState<T>(initial);
            states.Values.Add(key, made); states.Closers.Add(made.Close);
            return made;
        });
    }
    private sealed class HostStates
    {
        public readonly Dictionary<string, object> Values = new(StringComparer.Ordinal);
        public readonly List<Action> Closers = [];
        public void Close() { foreach (var close in Closers) close(); Closers.Clear(); Values.Clear(); }
    }
}
